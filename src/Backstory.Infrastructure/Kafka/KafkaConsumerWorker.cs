using System.Text;
using Backstory.Core.Messaging;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backstory.Infrastructure.Kafka;

/// <summary>
/// Background loop that reads one topic and feeds each message to a <see cref="MessageProcessor{T}"/>.
/// Every worker service (Indexer, Context, ...) runs one of these per topic it consumes.
///
/// Offsets ("how far this consumer group has read") are handled like this:
///   EnableAutoCommit = true        a background timer commits stored offsets every 5 s
///   EnableAutoOffsetStore = false  ...but an offset is only STORED after we finish the message
/// So a crash mid-message means the message is read again after restart: at-least-once delivery.
/// Handlers must therefore be idempotent (processing the same message twice changes nothing).
/// </summary>
public sealed class KafkaConsumerWorker<T> : BackgroundService
{
    private readonly string _topic;
    private readonly string _groupId;
    private readonly MessageProcessor<T> _processor;
    private readonly KafkaOptions _options;
    private readonly ILogger _logger;

    public KafkaConsumerWorker(string topic, string groupId, MessageProcessor<T> processor, KafkaOptions options, ILogger logger)
    {
        _topic = topic;
        _groupId = groupId;
        _processor = processor;
        _options = options;
        _logger = logger;
    }

    // Consume() blocks a thread, so run the loop off the host's startup path.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken ct)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            ClientId = _options.ClientId,
            // Consumers with the same GroupId share the topic's partitions between them (that is how we scale).
            GroupId = _groupId,
            // A brand-new group starts from the oldest message, so nothing published before startup is missed.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            // When a consumer joins or leaves, only the partitions that must move are paused.
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
        };

        using var consumer = new ConsumerBuilder<string?, byte[]>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Kafka consumer error on {Topic}: {Reason}", _topic, e.Reason))
            .SetPartitionsAssignedHandler((_, partitions) =>
                _logger.LogInformation("{Group} assigned {Topic} partitions [{Partitions}]",
                    _groupId, _topic, string.Join(",", partitions.Select(p => p.Partition.Value))))
            .Build();

        consumer.Subscribe(_topic);
        _logger.LogInformation("Consumer group {Group} subscribed to {Topic}", _groupId, _topic);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string?, byte[]>? result;
                try
                {
                    result = consumer.Consume(ct);
                }
                catch (ConsumeException ex)
                {
                    // e.g. topic does not exist yet. Log, back off, keep going.
                    _logger.LogError("Consume failed on {Topic}: {Reason}", _topic, ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                if (result?.Message is null)
                    continue;

                await _processor.ProcessAsync(ToRawMessage(result), ct);

                // Only now is the message "done". The next auto-commit will include it.
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            // Leaves the group cleanly (partitions are handed over at once) and commits stored offsets.
            consumer.Close();
            _logger.LogInformation("Consumer group {Group} on {Topic} stopped", _groupId, _topic);
        }
    }

    private static RawMessage ToRawMessage(ConsumeResult<string?, byte[]> result)
    {
        var headers = new Dictionary<string, string>();
        if (result.Message.Headers is not null)
        {
            foreach (var header in result.Message.Headers)
                headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes());
        }

        return new RawMessage(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            result.Message.Key,
            result.Message.Value ?? [],
            headers);
    }
}

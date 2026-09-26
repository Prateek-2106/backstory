using System.Text;
using Backstory.Core.Contracts;
using Backstory.Core.Messaging;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backstory.Infrastructure.Kafka;

/// <summary>
/// The one Kafka producer per process (producers are thread-safe and expensive, so share it).
/// Used for normal events (<see cref="IEventPublisher"/>) and for dead letters (<see cref="IDeadLetterSink"/>).
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDeadLetterSink, IDisposable
{
    private readonly IProducer<string?, byte[]> _producer;
    private readonly ILogger<KafkaEventPublisher> _logger;

    public KafkaEventPublisher(IOptions<KafkaOptions> options, ILogger<KafkaEventPublisher> logger)
    {
        _logger = logger;
        var config = new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            ClientId = options.Value.ClientId,

            // Durability: the broker confirms only once all in-sync replicas have the message.
            Acks = Acks.All,
            // No duplicates when the producer retries after a network blip.
            EnableIdempotence = true,
            // Batch for up to 5 ms and compress: much higher throughput for almost no latency.
            LingerMs = 5,
            CompressionType = CompressionType.Lz4,
        };

        _producer = new ProducerBuilder<string?, byte[]>(config)
            .SetErrorHandler((_, e) => _logger.LogError("Kafka producer error: {Reason} (fatal: {IsFatal})", e.Reason, e.IsFatal))
            .Build();
    }

    public async Task<PublishReceipt> PublishAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
    {
        var message = new Message<string?, byte[]>
        {
            Key = key,
            Value = EventSerializer.Serialize(envelope),
            // Headers let tools (and Kafka UI) see what a message is without parsing the body.
            Headers = new Headers
            {
                { "event-id", Encoding.UTF8.GetBytes(envelope.Id) },
                { "event-type", Encoding.UTF8.GetBytes(envelope.Type) },
                { "correlation-id", Encoding.UTF8.GetBytes(envelope.CorrelationId) },
            },
        };

        var result = await _producer.ProduceAsync(topic, message, ct);
        _logger.LogDebug("Published {EventType} {EventId} to {Topic}[{Partition}]@{Offset}",
            envelope.Type, envelope.Id, result.Topic, result.Partition.Value, result.Offset.Value);

        return new PublishReceipt(result.Topic, result.Partition.Value, result.Offset.Value);
    }

    public async Task SendAsync(DeadLetter letter, CancellationToken ct)
    {
        var headers = new Headers();
        foreach (var (name, value) in letter.Headers)
            headers.Add(name, Encoding.UTF8.GetBytes(value));

        var result = await PublishRawAsync(letter.Topic, letter.Key, letter.Value, headers, ct);
        _logger.LogWarning("Dead-lettered message to {Topic}[{Partition}]@{Offset}",
            result.Topic, result.Partition, result.Offset);
    }

    /// <summary>Send bytes as-is. Used for dead letters, and by the demo to send deliberately broken messages.</summary>
    public async Task<PublishReceipt> PublishRawAsync(string topic, string? key, byte[] value, Headers? headers = null, CancellationToken ct = default)
    {
        var result = await _producer.ProduceAsync(
            topic, new Message<string?, byte[]> { Key = key, Value = value, Headers = headers ?? new Headers() }, ct);
        return new PublishReceipt(result.Topic, result.Partition.Value, result.Offset.Value);
    }

    public void Dispose()
    {
        // Send anything still sitting in the batch buffer before shutting down.
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}

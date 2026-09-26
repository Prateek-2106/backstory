using System.Text;
using Backstory.Core.Contracts;
using Backstory.Core.Messaging;
using Backstory.Infrastructure.Kafka;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Backstory.Infrastructure.Tests.Kafka;

/// <summary>
/// Real Kafka, real producer, real consumer. Each test gets its own throwaway topic so tests
/// never see each other's messages and never touch the app's real topics.
///
/// Needs Kafka on localhost:9092 (docker compose up -d), or set KAFKA_BOOTSTRAP.
/// Run only unit tests with:  dotnet test --filter Category!=Integration
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaRoundTripTests : IAsyncLifetime
{
    private static readonly string Bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly string _topic = $"it-{Guid.NewGuid():N}";
    private readonly KafkaOptions _options = new()
    {
        BootstrapServers = Bootstrap,
        ClientId = "integration-tests",
        Retry = new RetryOptions { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(50) },
    };

    private string DeadLetterTopic => Topics.DeadLetter(_topic);

    public async Task InitializeAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Bootstrap }).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification { Name = _topic, NumPartitions = 2, ReplicationFactor = 1 },
            new TopicSpecification { Name = DeadLetterTopic, NumPartitions = 1, ReplicationFactor = 1 },
        ]);
    }

    public async Task DisposeAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Bootstrap }).Build();
        await admin.DeleteTopicsAsync([_topic, DeadLetterTopic]);
    }

    [Fact]
    public async Task PublishedEvent_IsConsumedAndHandled()
    {
        using var publisher = CreatePublisher();
        var handler = new SignalHandler(fail: false);
        var worker = CreateWorker(handler, publisher);
        var article = NewArticle();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var receipt = await publisher.PublishAsync(_topic, article.ArticleId,
                EventEnvelope<ArticlePublished>.Create("integration-tests", article));
            Assert.Equal(_topic, receipt.Topic);

            var (envelope, context) = await handler.FirstCall.WaitAsync(Timeout);

            Assert.Equal(article, envelope.Data);
            Assert.Equal(article.ArticleId, context.Key);
            Assert.Equal(receipt.Offset, context.Offset);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailingHandler_MessageLandsInDeadLetterTopicWithErrorHeaders()
    {
        using var publisher = CreatePublisher();
        var worker = CreateWorker(new SignalHandler(fail: true), publisher);
        var article = NewArticle();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await publisher.PublishAsync(_topic, article.ArticleId,
                EventEnvelope<ArticlePublished>.Create("integration-tests", article));

            var dead = await ReadOneAsync(DeadLetterTopic);

            Assert.Equal(article.ArticleId, dead.Message.Key);
            Assert.Equal(_topic, Header(dead, DeadLetterHeaders.OriginalTopic));
            Assert.Equal("2", Header(dead, DeadLetterHeaders.Attempts));
            Assert.Contains("simulated", Header(dead, DeadLetterHeaders.ErrorMessage), StringComparison.Ordinal);

            // The body is the original event, so it can be replayed once the bug is fixed.
            var replayed = EventSerializer.Deserialize<ArticlePublished>(dead.Message.Value);
            Assert.Equal(article, replayed.Data);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ---------- helpers ----------

    private KafkaEventPublisher CreatePublisher() =>
        new(Options.Create(_options), NullLogger<KafkaEventPublisher>.Instance);

    private KafkaConsumerWorker<ArticlePublished> CreateWorker(SignalHandler handler, KafkaEventPublisher deadLetters) =>
        new(_topic, groupId: $"it-group-{Guid.NewGuid():N}",
            new MessageProcessor<ArticlePublished>(handler, deadLetters, _options.Retry, NullLogger.Instance),
            _options, NullLogger.Instance);

    private static ArticlePublished NewArticle() => new(
        ArticleId: $"it-{Guid.NewGuid():N}",
        Headline: "Integration test headline",
        Summary: null, Body: null, Url: null, Section: "test",
        PublishedAt: new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    private static async Task<ConsumeResult<string?, byte[]>> ReadOneAsync(string topic)
    {
        using var consumer = new ConsumerBuilder<string?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = Bootstrap,
            GroupId = $"it-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(1)));
            if (result?.Message is not null)
                return result;
        }

        throw new TimeoutException($"No message arrived on {topic} within {Timeout.TotalSeconds}s.");
    }

    private static string Header(ConsumeResult<string?, byte[]> result, string name) =>
        Encoding.UTF8.GetString(result.Message.Headers.GetLastBytes(name));

    /// <summary>Records the first call; optionally always throws.</summary>
    private sealed class SignalHandler(bool fail) : IEventHandler<ArticlePublished>
    {
        private readonly TaskCompletionSource<(EventEnvelope<ArticlePublished>, MessageContext)> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<(EventEnvelope<ArticlePublished> Envelope, MessageContext Context)> FirstCall => _first.Task;

        public Task HandleAsync(EventEnvelope<ArticlePublished> envelope, MessageContext context, CancellationToken ct)
        {
            _first.TrySetResult((envelope, context));
            return fail ? throw new InvalidOperationException("simulated handler failure") : Task.CompletedTask;
        }
    }
}

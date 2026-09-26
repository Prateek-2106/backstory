using Backstory.Core.Contracts;

namespace Backstory.Core.Messaging;

// The messaging contracts every service codes against. None of these mention Kafka:
// Backstory.Infrastructure implements them with Kafka, tests implement them with fakes.

/// <summary>Publishes events. Implemented by KafkaEventPublisher.</summary>
public interface IEventPublisher
{
    /// <param name="key">Partition key. Same key = same partition = processed in order (we use articleId / documentId).</param>
    Task<PublishReceipt> PublishAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default);
}

/// <summary>Where the message landed. Handy for logs and the demo.</summary>
public sealed record PublishReceipt(string Topic, int Partition, long Offset);

/// <summary>Your business logic for one event type. Throw to signal failure; the processor retries and dead-letters.</summary>
public interface IEventHandler<T>
{
    Task HandleAsync(EventEnvelope<T> envelope, MessageContext context, CancellationToken ct);
}

/// <summary>Where a message came from. Useful in logs.</summary>
public sealed record MessageContext(string Topic, int Partition, long Offset, string? Key, int Attempt);

/// <summary>A message exactly as it came off the wire, before we try to parse it.</summary>
public sealed record RawMessage(
    string Topic,
    int Partition,
    long Offset,
    string? Key,
    byte[] Value,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>A message we gave up on, plus why.</summary>
public sealed record DeadLetter(string Topic, string? Key, byte[] Value, IReadOnlyDictionary<string, string> Headers);

/// <summary>Sends dead letters somewhere durable (a ".dlq" Kafka topic).</summary>
public interface IDeadLetterSink
{
    Task SendAsync(DeadLetter letter, CancellationToken ct);
}

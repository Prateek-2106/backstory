using System.Globalization;
using Backstory.Core.Contracts;
using Microsoft.Extensions.Logging;

namespace Backstory.Core.Messaging;

public enum ProcessingOutcome
{
    /// <summary>The handler succeeded (possibly after retries).</summary>
    Handled,

    /// <summary>We gave up and parked the message in the dead-letter topic.</summary>
    DeadLettered,
}

/// <summary>
/// What happens to ONE message after it is read from Kafka:
///
///   bytes ──parse──► bad?  ──► dead-letter immediately (retrying garbage never helps)
///            │
///            └─ ok ──► handler ──► success ──► Handled
///                        │
///                        └─ throws ──► wait, try again (up to MaxAttempts)
///                                        └─ still failing ──► dead-letter
///
/// Either outcome means "this message is finished", so the consumer may move past it.
/// If even the dead-letter write fails, the exception escapes and the message is NOT
/// marked done, so it will be read again after a restart (at-least-once delivery).
///
/// This class knows nothing about Kafka, which is why it can be unit-tested without a broker.
/// </summary>
public sealed class MessageProcessor<T>
{
    private readonly IEventHandler<T> _handler;
    private readonly IDeadLetterSink _deadLetters;
    private readonly RetryOptions _retry;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public MessageProcessor(
        IEventHandler<T> handler,
        IDeadLetterSink deadLetters,
        RetryOptions retry,
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null) // tests pass a no-op so they run instantly
    {
        _handler = handler;
        _deadLetters = deadLetters;
        _retry = retry;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public async Task<ProcessingOutcome> ProcessAsync(RawMessage message, CancellationToken ct)
    {
        // 1. Parse. A message that cannot be parsed will never succeed, so no retries.
        EventEnvelope<T> envelope;
        try
        {
            envelope = EventSerializer.Deserialize<T>(message.Value);
        }
        catch (InvalidEventException ex)
        {
            _logger.LogError("Poison message at {Topic}[{Partition}]@{Offset}: {Error}",
                message.Topic, message.Partition, message.Offset, ex.Message);
            await DeadLetterAsync(message, ex, attempts: 0, ct);
            return ProcessingOutcome.DeadLettered;
        }

        // 2. Handle, with retries and exponential backoff.
        for (var attempt = 1; ; attempt++)
        {
            var context = new MessageContext(message.Topic, message.Partition, message.Offset, message.Key, attempt);
            try
            {
                await _handler.HandleAsync(envelope, context, ct);
                return ProcessingOutcome.Handled;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutting down: not a failure, and the message is not marked done
            }
            catch (PermanentFailureException ex)
            {
                // The handler says retrying can never help (e.g. an untrusted URL): dead-letter now.
                _logger.LogError("Permanent failure for event {EventId}: {Error}; sending to dead-letter topic without retrying.",
                    envelope.Id, ex.Message);
                await DeadLetterAsync(message, ex, attempt, ct);
                return ProcessingOutcome.DeadLettered;
            }
            catch (Exception ex) when (attempt < _retry.MaxAttempts)
            {
                var wait = _retry.DelayAfter(attempt);
                _logger.LogWarning("Attempt {Attempt}/{Max} failed for event {EventId}: {Error}. Retrying in {Delay}ms.",
                    attempt, _retry.MaxAttempts, envelope.Id, ex.Message, wait.TotalMilliseconds);
                await _delay(wait, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Giving up on event {EventId} after {Attempts} attempts; sending to dead-letter topic.",
                    envelope.Id, attempt);
                await DeadLetterAsync(message, ex, attempt, ct);
                return ProcessingOutcome.DeadLettered;
            }
        }
    }

    private Task DeadLetterAsync(RawMessage message, Exception error, int attempts, CancellationToken ct)
    {
        // Keep the original headers and bytes untouched, and add why and where it failed,
        // so someone can inspect it later and replay it once the bug is fixed.
        var headers = new Dictionary<string, string>(message.Headers)
        {
            [DeadLetterHeaders.OriginalTopic] = message.Topic,
            [DeadLetterHeaders.OriginalPartition] = message.Partition.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.OriginalOffset] = message.Offset.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.ErrorType] = error.GetType().FullName ?? error.GetType().Name,
            [DeadLetterHeaders.ErrorMessage] = Truncate(error.Message, 1000),
            [DeadLetterHeaders.Attempts] = attempts.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.FailedAt] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        return _deadLetters.SendAsync(
            new DeadLetter(Topics.DeadLetter(message.Topic), message.Key, message.Value, headers), ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// Throw from a handler when retrying cannot possibly succeed (bad data, policy violation).
/// The processor dead-letters the message immediately instead of retrying it.
/// Any other exception is treated as temporary (network, database down) and retried.
/// </summary>
public sealed class PermanentFailureException : Exception
{
    public PermanentFailureException() { }
    public PermanentFailureException(string message) : base(message) { }
    public PermanentFailureException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Header names added to every dead-lettered message.</summary>
public static class DeadLetterHeaders
{
    public const string OriginalTopic = "x-original-topic";
    public const string OriginalPartition = "x-original-partition";
    public const string OriginalOffset = "x-original-offset";
    public const string ErrorType = "x-error-type";
    public const string ErrorMessage = "x-error-message";
    public const string Attempts = "x-attempts";
    public const string FailedAt = "x-failed-at";
}

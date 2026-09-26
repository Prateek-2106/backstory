using System.Text;
using Backstory.Core.Contracts;
using Backstory.Core.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backstory.Core.Tests.Messaging;

public class MessageProcessorTests
{
    // ---------- fakes: no Kafka needed ----------

    /// <summary>Fails the first <c>failTimes</c> calls, then succeeds.</summary>
    private sealed class FlakyHandler(int failTimes) : IEventHandler<ArticlePublished>
    {
        public List<MessageContext> Calls { get; } = [];

        public Task HandleAsync(EventEnvelope<ArticlePublished> envelope, MessageContext context, CancellationToken ct)
        {
            Calls.Add(context);
            if (Calls.Count <= failTimes)
                throw new TimeoutException($"simulated failure {Calls.Count}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDeadLetterSink : IDeadLetterSink
    {
        public List<DeadLetter> Letters { get; } = [];

        public Task SendAsync(DeadLetter letter, CancellationToken ct)
        {
            Letters.Add(letter);
            return Task.CompletedTask;
        }
    }

    private sealed class BrokenDeadLetterSink : IDeadLetterSink
    {
        public Task SendAsync(DeadLetter letter, CancellationToken ct) => throw new IOException("Kafka unreachable");
    }

    // ---------- helpers ----------

    private readonly RecordingDeadLetterSink _deadLetters = new();
    private readonly List<TimeSpan> _delays = [];

    private MessageProcessor<ArticlePublished> CreateProcessor(IEventHandler<ArticlePublished> handler, IDeadLetterSink? sink = null) =>
        new(handler, sink ?? _deadLetters, new RetryOptions { MaxAttempts = 3, InitialDelay = TimeSpan.FromMilliseconds(100) },
            NullLogger.Instance,
            delay: (wait, _) => { _delays.Add(wait); return Task.CompletedTask; }); // record waits instead of sleeping

    private static RawMessage ValidMessage()
    {
        var article = new ArticlePublished("a-1", "Headline", null, null, null, null, DateTimeOffset.UnixEpoch);
        var bytes = EventSerializer.Serialize(EventEnvelope<ArticlePublished>.Create("test", article));
        return new RawMessage(Topics.Articles, Partition: 2, Offset: 41, Key: "a-1", bytes,
            new Dictionary<string, string> { ["correlation-id"] = "corr-1" });
    }

    // ---------- tests ----------

    [Fact]
    public async Task Handler_SucceedsFirstTime_IsHandledWithOneCall()
    {
        var handler = new FlakyHandler(failTimes: 0);

        var outcome = await CreateProcessor(handler).ProcessAsync(ValidMessage(), CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Handled, outcome);
        Assert.Single(handler.Calls);
        Assert.Empty(_deadLetters.Letters);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Handler_FailsTwiceThenSucceeds_IsHandledAfterBackoff()
    {
        var handler = new FlakyHandler(failTimes: 2);

        var outcome = await CreateProcessor(handler).ProcessAsync(ValidMessage(), CancellationToken.None);

        Assert.Equal(ProcessingOutcome.Handled, outcome);
        Assert.Equal(new[] { 1, 2, 3 }, handler.Calls.Select(c => c.Attempt));
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200) }, _delays); // exponential
        Assert.Empty(_deadLetters.Letters);
    }

    [Fact]
    public async Task Handler_AlwaysFails_IsDeadLetteredWithErrorDetails()
    {
        var handler = new FlakyHandler(failTimes: int.MaxValue);
        var message = ValidMessage();

        var outcome = await CreateProcessor(handler).ProcessAsync(message, CancellationToken.None);

        Assert.Equal(ProcessingOutcome.DeadLettered, outcome);
        Assert.Equal(3, handler.Calls.Count);

        var letter = Assert.Single(_deadLetters.Letters);
        Assert.Equal("news.articles.v1.dlq", letter.Topic);
        Assert.Equal("a-1", letter.Key);                    // same key: replay lands on the same partition
        Assert.Equal(message.Value, letter.Value);          // original bytes untouched
        Assert.Equal("corr-1", letter.Headers["correlation-id"]); // original headers kept
        Assert.Equal("3", letter.Headers[DeadLetterHeaders.Attempts]);
        Assert.Equal("41", letter.Headers[DeadLetterHeaders.OriginalOffset]);
        Assert.Equal("2", letter.Headers[DeadLetterHeaders.OriginalPartition]);
        Assert.Equal("System.TimeoutException", letter.Headers[DeadLetterHeaders.ErrorType]);
        Assert.Equal("simulated failure 3", letter.Headers[DeadLetterHeaders.ErrorMessage]);
    }

    [Fact]
    public async Task PoisonMessage_IsDeadLetteredImmediately_WithoutCallingHandler()
    {
        var handler = new FlakyHandler(failTimes: 0);
        var poison = ValidMessage() with { Value = Encoding.UTF8.GetBytes("this is not JSON") };

        var outcome = await CreateProcessor(handler).ProcessAsync(poison, CancellationToken.None);

        Assert.Equal(ProcessingOutcome.DeadLettered, outcome);
        Assert.Empty(handler.Calls);
        Assert.Empty(_delays); // no point retrying garbage
        var letter = Assert.Single(_deadLetters.Letters);
        Assert.Equal("0", letter.Headers[DeadLetterHeaders.Attempts]);
        Assert.Equal(typeof(InvalidEventException).FullName, letter.Headers[DeadLetterHeaders.ErrorType]);
    }

    [Fact]
    public async Task DeadLetterSinkFails_ExceptionEscapes_SoOffsetIsNotStored()
    {
        var handler = new FlakyHandler(failTimes: int.MaxValue);
        var processor = CreateProcessor(handler, new BrokenDeadLetterSink());

        await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(ValidMessage(), CancellationToken.None));
    }

    [Fact]
    public async Task Shutdown_DuringHandler_IsNotTreatedAsFailure()
    {
        using var cts = new CancellationTokenSource();
        var handler = new CancellingHandler(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProcessor(handler).ProcessAsync(ValidMessage(), cts.Token));

        Assert.Empty(_deadLetters.Letters);
        Assert.Empty(_delays);
    }

    private sealed class CancellingHandler(CancellationTokenSource cts) : IEventHandler<ArticlePublished>
    {
        public Task HandleAsync(EventEnvelope<ArticlePublished> envelope, MessageContext context, CancellationToken ct)
        {
            cts.Cancel(); // host shutting down mid-message
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void RetryOptions_DelayDoublesEachAttempt()
    {
        var retry = new RetryOptions { InitialDelay = TimeSpan.FromMilliseconds(500) };

        Assert.Equal(TimeSpan.FromMilliseconds(500), retry.DelayAfter(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), retry.DelayAfter(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2000), retry.DelayAfter(3));
    }
}

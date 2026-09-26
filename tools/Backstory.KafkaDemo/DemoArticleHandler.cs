using Backstory.Core.Contracts;
using Backstory.Core.Messaging;

namespace Backstory.KafkaDemo;

/// <summary>
/// Stand-in for real business logic. In step 6 the Context Worker's handler does the RAG work here.
/// Put a breakpoint on the first line of HandleAsync and hover over envelope / context.
/// </summary>
public sealed class DemoArticleHandler(DemoState state) : IEventHandler<ArticlePublished>
{
    public Task HandleAsync(EventEnvelope<ArticlePublished> envelope, MessageContext context, CancellationToken ct)
    {
        var article = envelope.Data;
        Console.WriteLine(
            $"   handling {article.ArticleId} \"{article.Headline}\" from partition {context.Partition}, offset {context.Offset} (attempt {context.Attempt})");

        state.RecordCall();

        if (state.FailEveryTime)
            throw new InvalidOperationException($"Simulated failure for {article.ArticleId}");

        return Task.CompletedTask;
    }
}

/// <summary>Shared between Program and the handler so the demo knows when it is finished.</summary>
public sealed class DemoState(bool failEveryTime)
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public bool FailEveryTime { get; } = failEveryTime;
    public int ExpectedCalls { get; private set; } = int.MaxValue;
    public int Calls => Volatile.Read(ref _calls);
    public Task Done => _done.Task;

    public void ExpectCalls(int expected)
    {
        ExpectedCalls = expected;
        if (Calls >= expected) _done.TrySetResult();
    }

    public void RecordCall()
    {
        if (Interlocked.Increment(ref _calls) >= ExpectedCalls) _done.TrySetResult();
    }
}

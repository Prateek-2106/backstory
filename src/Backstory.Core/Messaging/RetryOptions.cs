namespace Backstory.Core.Messaging;

/// <summary>How hard to try before dead-lettering. Bound from config section "Kafka:Retry".</summary>
public sealed class RetryOptions
{
    /// <summary>Total tries, including the first. 3 = first try + 2 retries.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait after the first failure; doubles each time (500ms, 1s, 2s, ...).</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan DelayAfter(int failedAttempt) =>
        TimeSpan.FromMilliseconds(InitialDelay.TotalMilliseconds * Math.Pow(2, failedAttempt - 1));
}

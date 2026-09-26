using System.Collections.Concurrent;

namespace Backstory.Core.Ingestion;

/// <summary>
/// Politeness: spaces out requests to the same host so we never hammer a source.
/// 30 requests/minute = at least 2 s between requests to that host. Different hosts don't wait for each other.
/// </summary>
public sealed class DomainRateLimiter
{
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ConcurrentDictionary<string, HostSlot> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public DomainRateLimiter(TimeProvider time) : this(time, (d, ct) => Task.Delay(d, time, ct)) { }

    /// <summary>Tests pass their own delay so nothing actually sleeps.</summary>
    public DomainRateLimiter(TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _time = time;
        _delay = delay;
    }

    public async Task WaitAsync(string host, int maxRequestsPerMinute, CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(1) / Math.Max(1, maxRequestsPerMinute);
        var slot = _hosts.GetOrAdd(host, _ => new HostSlot());

        await slot.Gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (slot.NextAllowed > now)
            {
                await _delay(slot.NextAllowed - now, ct);
                now = slot.NextAllowed;
            }
            slot.NextAllowed = now + interval;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private sealed class HostSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset NextAllowed { get; set; } = DateTimeOffset.MinValue;
    }
}

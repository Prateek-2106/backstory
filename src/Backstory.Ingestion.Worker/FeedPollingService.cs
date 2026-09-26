using System.Collections.Concurrent;
using Backstory.Core.Ingestion;

namespace Backstory.Ingestion.Worker;

/// <summary>
/// Runs one polling loop per feed: poll now, wait PollInterval, repeat.
/// A failing feed never stops the others; each loop catches and logs its own errors.
/// </summary>
public sealed class FeedPollingService(
    IngestionPipeline pipeline,
    IngestionOptions options,
    FeedStatusBoard board,
    ILogger<FeedPollingService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // C1: drop source feeds whose own URL is untrusted before anything is fetched.
        var feeds = pipeline.ValidateFeeds(options.Feeds);
        logger.LogInformation("Polling {Count} feed(s): {Feeds}", feeds.Count, string.Join(", ", feeds.Select(f => f.Id)));

        // Stagger the first polls by 3 s so we don't hit every site in the same instant.
        var loops = feeds.Select((feed, i) => PollForeverAsync(feed, TimeSpan.FromSeconds(3 * i), stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task PollForeverAsync(FeedDefinition feed, TimeSpan initialDelay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(initialDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    board.Record(await pipeline.PollAsync(feed, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // e.g. Kafka unreachable. Log, keep the loop alive, try again next interval.
                    logger.LogError(ex, "Poll of {FeedId} failed", feed.Id);
                    board.Record(new FeedPollResult(feed.Id, DateTimeOffset.UtcNow, false, ex.Message, 0, 0, 0, 0, 0, 0, []));
                }

                await Task.Delay(feed.PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
    }
}

/// <summary>Latest poll result per feed, served at GET /ingestion/status.</summary>
public sealed class FeedStatusBoard
{
    private readonly ConcurrentDictionary<string, FeedPollResult> _latest = new(StringComparer.Ordinal);

    public void Record(FeedPollResult result) => _latest[result.FeedId] = result;

    public IReadOnlyList<FeedPollResult> Snapshot() => _latest.Values.OrderBy(r => r.FeedId, StringComparer.Ordinal).ToList();
}

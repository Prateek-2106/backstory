using Backstory.Core.Contracts;
using Backstory.Core.Messaging;
using Backstory.Core.Trust;
using Microsoft.Extensions.Logging;

namespace Backstory.Core.Ingestion;

/// <summary>What one poll of one feed did. Shown at GET /ingestion/status and in logs.</summary>
public sealed record FeedPollResult(
    string FeedId,
    DateTimeOffset PolledAt,
    bool FeedFetched,
    string? FeedError,
    int Items,
    int Published,
    int AlreadySeen,
    int Untrusted,
    int Rejected,
    int Failed,
    IReadOnlyList<string> SkipReasons,
    int SummaryFallbacks = 0);

/// <summary>
/// One poll of one feed: fetch → parse → for each new item, run the trust checkpoints → publish.
/// The design doc's diagram for step 4 is this class, top to bottom.
/// </summary>
public sealed class IngestionPipeline
{
    private const string ServiceName = "ingestion-worker";

    private readonly SafeFetcher _fetcher;
    private readonly TrustRegistry _trust;
    private readonly SeenUrlCache _seen;
    private readonly IEventPublisher _publisher;
    private readonly IngestionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<IngestionPipeline> _logger;

    public IngestionPipeline(SafeFetcher fetcher, TrustRegistry trust, SeenUrlCache seen, IEventPublisher publisher,
        IngestionOptions options, TimeProvider time, ILogger<IngestionPipeline> logger)
    {
        _fetcher = fetcher;
        _trust = trust;
        _seen = seen;
        _publisher = publisher;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// C1: run once at startup. A source feed whose own URL is untrusted is a config mistake:
    /// disable it loudly rather than silently fetching from somewhere we don't trust.
    /// </summary>
    public IReadOnlyList<FeedDefinition> ValidateFeeds(IEnumerable<FeedDefinition> feeds)
    {
        var enabled = new List<FeedDefinition>();
        foreach (var feed in feeds.Where(f => f.Enabled))
        {
            if (feed.Kind == FeedKind.Source && _trust.Evaluate(feed.Url) is { IsTrusted: false } decision)
            {
                _logger.LogError("C1: feed '{FeedId}' disabled: its URL {Url} is not trusted ({Reason})",
                    feed.Id, feed.Url, decision.Reason);
                continue;
            }
            enabled.Add(feed);
        }
        return enabled;
    }

    public async Task<FeedPollResult> PollAsync(FeedDefinition feed, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var reasons = new List<string>();
        int published = 0, seen = 0, untrusted = 0, rejected = 0, failed = 0, fallbacks = 0;

        // Feeds themselves go through SafeFetcher (so a source feed that redirects off the allowlist is refused).
        // Newsroom feeds are our own content and are not on the allowlist, so they skip the trust gate.
        var feedResult = feed.Kind == FeedKind.Source
            ? await _fetcher.FetchAsync(feed.Url, FetchKind.Feed, ct)
            : await _fetcher.FetchUntrustedFeedAsync(feed.Url, ct);

        if (!feedResult.IsFetched)
            return new FeedPollResult(feed.Id, now, false, feedResult.Reason, 0, 0, 0, 0, 0, 0, []);

        IReadOnlyList<FeedItem> items;
        try
        {
            items = FeedParser.Parse(feedResult.Body!, new Uri(feedResult.FinalUrl!));
        }
        catch (FeedFormatException ex)
        {
            return new FeedPollResult(feed.Id, now, false, ex.Message, 0, 0, 0, 0, 0, 0, []);
        }

        foreach (var item in items.Take(_options.MaxItemsPerPoll))
        {
            // Keyed per feed kind: the same URL can legitimately be both a newsroom headline and
            // a source document (e.g. an NPR story in both NPR feeds), and one must not hide the other.
            var key = SeenKey(feed.Kind, item.Link);
            if (_seen.Contains(key))
            {
                seen++;
                continue;
            }

            if (feed.Kind == FeedKind.Newsroom)
            {
                await PublishArticleAsync(feed, item, now, ct);
                _seen.Add(key);
                published++;
                continue;
            }

            // C2: the feed is trusted, but is THIS link? (feeds link to partners, ads, other sites)
            var decision = _trust.Evaluate(item.Link);
            if (!decision.IsTrusted)
            {
                untrusted++;
                reasons.Add($"C2 untrusted link {item.Link}: {decision.Reason}");
                _logger.LogInformation("C2: skipped {Url}: {Reason}", item.Link, decision.Reason);
                _seen.Add(key); // it will be untrusted next time too; don't log it every poll
                continue;
            }

            SourceDocumentFetched document;
            if (!decision.Source!.AllowFullText)
            {
                // C5: we may cite this source but not copy its articles. Use only what it already
                // published in its own feed, and never request the page.
                if (item.Summary is null)
                {
                    rejected++;
                    reasons.Add($"C5 {item.Link}: summary-only source and the feed item has no summary");
                    _seen.Add(key);
                    continue;
                }
                document = BuildDocument(item.Link, decision.Host!, item.Title, item.Summary, item.PublishedAt, now, "rss-summary");
            }
            else
            {
                // C3 + C4 happen inside the fetcher (redirect hops, robots.txt, content type, size).
                var page = await _fetcher.FetchAsync(item.Link, FetchKind.Page, ct);
                if (page.Status == FetchStatus.Rejected)
                {
                    rejected++;
                    reasons.Add($"{item.Link}: {page.Reason}");
                    _seen.Add(key);
                    continue;
                }
                if (page.Status == FetchStatus.Failed)
                {
                    failed++; // not marked seen: try again on the next poll
                    reasons.Add($"{item.Link}: {page.Reason}");
                    continue;
                }

                var extracted = HtmlTextExtractor.Extract(page.Body!);
                var usedPage = extracted.Text.Length >= _options.MinTextLength;
                if (!usedPage)
                {
                    // Visible on purpose: a silent fallback hid an extractor bug once already.
                    fallbacks++;
                    _logger.LogWarning("Extracted only {Chars} chars from {Url}; using the feed summary instead",
                        extracted.Text.Length, page.FinalUrl);
                }

                var text = usedPage ? extracted.Text : item.Summary ?? extracted.Text;
                var finalHost = new Uri(page.FinalUrl!).Host;
                document = BuildDocument(page.FinalUrl!, finalHost, extracted.Title ?? item.Title, text,
                    extracted.PublishedAt ?? item.PublishedAt, now, usedPage ? "rss-page" : "rss-page-fallback");
                _seen.Add(SeenKey(feed.Kind, page.FinalUrl!));
            }

            await _publisher.PublishAsync(Topics.SourceDocuments, document.DocumentId,
                EventEnvelope<SourceDocumentFetched>.Create(ServiceName, document), ct);
            _seen.Add(key);
            published++;
        }

        var result = new FeedPollResult(feed.Id, now, true, null, items.Count, published, seen, untrusted, rejected, failed, reasons, fallbacks);
        _logger.LogInformation(
            "Polled {FeedId}: {Items} items, {Published} published ({Fallbacks} with summary fallback), {Seen} already seen, {Untrusted} untrusted, {Rejected} rejected, {Failed} failed",
            feed.Id, result.Items, published, fallbacks, seen, untrusted, rejected, failed);
        return result;
    }

    private static string SeenKey(FeedKind kind, string url) => $"{kind}:{UrlIdentity.Normalize(url)}";

    private static SourceDocumentFetched BuildDocument(string url, string host, string title, string text,
        DateTimeOffset? publishedAt, DateTimeOffset now, string origin) =>
        new(UrlIdentity.DocumentId(url), url, host, title, text, publishedAt, now, origin);

    private Task PublishArticleAsync(FeedDefinition feed, FeedItem item, DateTimeOffset now, CancellationToken ct)
    {
        var article = new ArticlePublished(
            ArticleId: UrlIdentity.ArticleId(item.Link),
            Headline: item.Title,
            Summary: item.Summary,
            Body: null,
            Url: item.Link,
            Section: feed.Section,
            PublishedAt: item.PublishedAt ?? now);

        return _publisher.PublishAsync(Topics.Articles, article.ArticleId,
            EventEnvelope<ArticlePublished>.Create(ServiceName, article), ct);
    }
}

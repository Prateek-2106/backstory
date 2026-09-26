using Backstory.Core.Contracts;
using Backstory.Core.Ingestion;
using Backstory.Core.Messaging;
using Backstory.Core.Trust;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backstory.Core.Tests.Ingestion;

/// <summary>The whole step 4 flow against a pretend internet and a pretend Kafka.</summary>
public class IngestionPipelineTests
{
    private static readonly TrustRegistry Trust = new(
    [
        new TrustedSource("un.org", "United Nations", TrustTier.Primary),                 // full text allowed
        new TrustedSource("bbc.co.uk", "BBC", TrustTier.Wire, AllowFullText: false),     // summary only
    ]);

    private const string SourceFeedUrl = "https://news.un.org/feed/rss.xml";

    private const string SourceFeed = """
        <rss version="2.0"><channel>
          <item><title>Security Council meets on Sudan</title><link>https://news.un.org/en/story/1</link>
                <description>The Council met on Tuesday.</description><pubDate>Tue, 22 Sep 2026 14:00:00 GMT</pubDate></item>
          <item><title>BBC explainer</title><link>https://www.bbc.co.uk/news/world-1</link>
                <description>&lt;p&gt;What we know about the talks.&lt;/p&gt;</description></item>
          <item><title>Sponsored</title><link>https://ads.example.com/buy-now</link></item>
        </channel></rss>
        """;

    private const string UnStoryHtml = """
        <html><head><title>Sudan | UN News</title><meta property="og:title" content="Security Council meets on Sudan"></head>
        <body><nav>Home World Africa Climate Health</nav>
        <article><h1>Security Council meets on Sudan</h1>
          <p>The UN Security Council met on Tuesday to discuss the humanitarian situation in Sudan and the ceasefire talks.</p>
          <p>Members called for unimpeded humanitarian access across all regions affected by the fighting since April 2023.</p>
        </article><footer>Copyright United Nations. All rights reserved worldwide.</footer></body></html>
        """;

    private readonly FakeHttp _web = new();
    private readonly RecordingPublisher _kafka = new();
    private readonly SeenUrlCache _seen = new();

    private IngestionPipeline CreatePipeline()
    {
        var options = new IngestionOptions { MinTextLength = 50 };
        var fetcher = new SafeFetcher(new HttpClient(_web), Trust,
            new DomainRateLimiter(TimeProvider.System, (_, _) => Task.CompletedTask),
            options, TimeProvider.System, NullLogger<SafeFetcher>.Instance);
        return new IngestionPipeline(fetcher, Trust, _seen, _kafka, options, TimeProvider.System,
            NullLogger<IngestionPipeline>.Instance);
    }

    private static FeedDefinition SourceFeedDef => new() { Id = "un", Url = SourceFeedUrl, Kind = FeedKind.Source };

    [Fact]
    public async Task SourceFeed_EachItemTakesTheRightPath()
    {
        _web.Xml(SourceFeedUrl, SourceFeed).Html("https://news.un.org/en/story/1", UnStoryHtml);

        var result = await CreatePipeline().PollAsync(SourceFeedDef, default);

        Assert.True(result.FeedFetched, result.FeedError);
        Assert.Equal(3, result.Items);
        Assert.Equal(2, result.Published);
        Assert.Equal(1, result.Untrusted); // the ad link (C2)

        var docs = _kafka.Documents;
        Assert.Equal(2, docs.Count);

        // Full-text source: page fetched, main text extracted, nav/footer gone.
        var un = docs.Single(d => d.Domain == "news.un.org");
        Assert.Equal("rss-page", un.Origin);
        Assert.Equal("Security Council meets on Sudan", un.Title);
        Assert.Contains("unimpeded humanitarian access", un.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright", un.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Home World Africa", un.Text, StringComparison.Ordinal);

        // C5: summary-only source: feed summary used (HTML stripped), page NEVER requested.
        var bbc = docs.Single(d => d.Domain == "www.bbc.co.uk");
        Assert.Equal("rss-summary", bbc.Origin);
        Assert.Contains("What we know about the talks.", bbc.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.bbc.co.uk/news/world-1", _web.Requested);

        // C2: the untrusted link was never contacted.
        Assert.DoesNotContain(_web.Requested, u => u.Contains("example.com", StringComparison.Ordinal));

        // Events keyed by DocumentId, so re-fetching overwrites instead of duplicating.
        Assert.All(_kafka.Published, p => Assert.Equal(Topics.SourceDocuments, p.Topic));
        Assert.Equal(UrlIdentity.DocumentId("https://news.un.org/en/story/1"), un.DocumentId);
    }

    [Fact]
    public async Task SecondPoll_PublishesNothingNew()
    {
        _web.Xml(SourceFeedUrl, SourceFeed).Html("https://news.un.org/en/story/1", UnStoryHtml);
        var pipeline = CreatePipeline();

        await pipeline.PollAsync(SourceFeedDef, default);
        var second = await pipeline.PollAsync(SourceFeedDef, default);

        Assert.Equal(0, second.Published);
        Assert.Equal(3, second.AlreadySeen);
        Assert.Equal(2, _kafka.Published.Count);
    }

    [Fact]
    public async Task FailedPage_IsRetriedOnNextPoll()
    {
        _web.Xml(SourceFeedUrl, SourceFeed)
            .Status("https://news.un.org/en/story/1", System.Net.HttpStatusCode.ServiceUnavailable);
        var pipeline = CreatePipeline();

        var first = await pipeline.PollAsync(SourceFeedDef, default);
        Assert.Equal(1, first.Failed);

        _web.Html("https://news.un.org/en/story/1", UnStoryHtml); // site recovers
        var second = await pipeline.PollAsync(SourceFeedDef, default);

        Assert.Equal(1, second.Published);
    }

    [Fact]
    public void C1_UntrustedSourceFeed_IsDisabledAtStartup()
    {
        var feeds = new[]
        {
            SourceFeedDef,
            new FeedDefinition { Id = "shady", Url = "https://shady-news.example/rss", Kind = FeedKind.Source },
            new FeedDefinition { Id = "newsroom", Url = "https://our-site.example/rss", Kind = FeedKind.Newsroom }, // not checked
        };

        var enabled = CreatePipeline().ValidateFeeds(feeds);

        Assert.Equal(new[] { "un", "newsroom" }, enabled.Select(f => f.Id));
    }

    [Fact]
    public async Task NewsroomFeed_PublishesArticles_WithoutTrustCheck()
    {
        const string url = "https://our-site.example/rss";
        _web.Xml(url, """
            <rss><channel><item><title>Court blocks tariff ruling appeal</title>
            <link>https://our-site.example/politics/tariffs</link><description>An appeals court declined.</description>
            </item></channel></rss>
            """);
        var feed = new FeedDefinition { Id = "newsroom", Url = url, Kind = FeedKind.Newsroom, Section = "politics" };

        var result = await CreatePipeline().PollAsync(feed, default);

        Assert.Equal(1, result.Published);
        var (topic, key, article) = Assert.Single(_kafka.Articles);
        Assert.Equal(Topics.Articles, topic);
        Assert.Equal(article.ArticleId, key);
        Assert.Equal("Court blocks tariff ruling appeal", article.Headline);
        Assert.Equal("politics", article.Section);
    }

    [Fact]
    public async Task SameUrlInNewsroomAndSourceFeeds_IsPublishedAsBoth()
    {
        // Regression: NPR stories appear in both the newsroom feed and the NPR World source feed.
        // A shared "seen" cache made the source feed skip them after the newsroom feed ran first.
        const string newsroomUrl = "https://our-site.example/rss";
        _web.Xml(newsroomUrl, """
                <rss><channel><item><title>Security Council meets on Sudan</title>
                <link>https://www.bbc.co.uk/news/world-1</link></item></channel></rss>
                """)
            .Xml(SourceFeedUrl, SourceFeed)
            .Html("https://news.un.org/en/story/1", UnStoryHtml);
        var pipeline = CreatePipeline();

        await pipeline.PollAsync(new FeedDefinition { Id = "newsroom", Url = newsroomUrl, Kind = FeedKind.Newsroom }, default);
        var source = await pipeline.PollAsync(SourceFeedDef, default);

        Assert.Equal(0, source.AlreadySeen);
        Assert.Contains(_kafka.Documents, d => d.Url == "https://www.bbc.co.uk/news/world-1");
        Assert.Contains(_kafka.Articles, a => a.Article.Url == "https://www.bbc.co.uk/news/world-1");
    }

    [Fact]
    public async Task PageWithTooLittleText_FallsBackToSummary_AndSaysSo()
    {
        _web.Xml(SourceFeedUrl, SourceFeed)
            .Html("https://news.un.org/en/story/1", "<html><body><p>Too short.</p></body></html>");

        var result = await CreatePipeline().PollAsync(SourceFeedDef, default);

        Assert.Equal(1, result.SummaryFallbacks);
        var un = _kafka.Documents.Single(d => d.Domain == "news.un.org");
        Assert.Equal("rss-page-fallback", un.Origin);   // not silently labelled "rss-page"
        Assert.Equal("The Council met on Tuesday.", un.Text);
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<(string Topic, string Key, object Data)> Published { get; } = [];

        public List<SourceDocumentFetched> Documents => Published.Select(p => p.Data).OfType<SourceDocumentFetched>().ToList();

        public List<(string Topic, string Key, ArticlePublished Article)> Articles =>
            Published.Where(p => p.Data is ArticlePublished).Select(p => (p.Topic, p.Key, (ArticlePublished)p.Data)).ToList();

        public Task<PublishReceipt> PublishAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
        {
            Published.Add((topic, key, envelope.Data!));
            return Task.FromResult(new PublishReceipt(topic, 0, Published.Count - 1));
        }
    }
}

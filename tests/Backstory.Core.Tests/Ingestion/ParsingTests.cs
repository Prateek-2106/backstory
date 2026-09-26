using Backstory.Core.Ingestion;

namespace Backstory.Core.Tests.Ingestion;

public class FeedParserTests
{
    private static readonly Uri FeedUrl = new("https://news.un.org/feed/rss.xml");

    [Fact]
    public void Rss2_ParsesItems_StripsHtml_ResolvesRelativeLinks()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel><title>UN News</title>
              <item><title>Talks resume &amp; progress</title><link>/en/story/1</link>
                    <description><![CDATA[<p>Delegates <b>met</b> again.</p>]]></description>
                    <pubDate>Sat, 26 Sep 2026 14:05:00 EDT</pubDate></item>
              <item><title>No link here</title></item>
            </channel></rss>
            """;

        var item = Assert.Single(FeedParser.Parse(xml, FeedUrl));

        Assert.Equal("Talks resume & progress", item.Title);
        Assert.Equal("https://news.un.org/en/story/1", item.Link);
        Assert.Equal("Delegates met again.", item.Summary);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 14, 5, 0, TimeSpan.FromHours(-4)), item.PublishedAt);
    }

    [Fact]
    public void Atom_ParsesAlternateLinkAndIsoDate()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><title>WHO update</title>
                <link rel="self" href="https://www.who.int/api/1"/>
                <link rel="alternate" href="https://www.who.int/news/item/1"/>
                <summary>Cases fell.</summary><updated>2026-09-25T08:30:00Z</updated></entry>
            </feed>
            """;

        var item = Assert.Single(FeedParser.Parse(xml, FeedUrl));

        Assert.Equal("https://www.who.int/news/item/1", item.Link);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 8, 30, 0, TimeSpan.Zero), item.PublishedAt);
    }

    [Theory]
    [InlineData("Sat, 26 Sep 2026 14:05:00 GMT", 14)]
    [InlineData("Sat, 26 Sep 2026 14:05:00 +0000", 14)]
    [InlineData("Sat, 26 Sep 2026 10:05:00 -0400", 14)]
    [InlineData("26 Sep 2026 07:05:00 PDT", 14)]
    [InlineData("2026-09-26T14:05:00Z", 14)]
    public void ParseDate_HandlesCommonFeedFormats(string value, int expectedUtcHour)
    {
        var parsed = FeedParser.ParseDate(value);

        Assert.Equal(expectedUtcHour, parsed?.UtcDateTime.Hour); // null would fail too
    }

    [Fact]
    public void MalformedXml_ThrowsFeedFormatException()
    {
        Assert.Throws<FeedFormatException>(() => FeedParser.Parse("<rss><channel>", FeedUrl));
    }

    [Fact]
    public void Dtd_IsIgnored_NoEntityExpansion()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY boom "BOOM">]>
            <rss><channel><item><title>Safe</title><link>https://news.un.org/1</link></item></channel></rss>
            """;

        var item = Assert.Single(FeedParser.Parse(xml, FeedUrl));
        Assert.Equal("Safe", item.Title);
    }
}

public class HtmlTextExtractorTests
{
    [Fact]
    public void Extract_PrefersArticle_DropsChromeScriptsAndShortLines()
    {
        const string html = """
            <html><head><title>Fallback | Site</title>
              <meta content="Clean headline" property="og:title">
              <meta property="article:published_time" content="2026-09-20T10:00:00Z">
              <script>var tracking = "this sentence is inside a script tag and must go";</script>
            </head><body>
              <header>Site name and a tagline that is quite long indeed for a header</header>
              <nav><ul><li>Home</li><li>World</li></ul></nav>
              <article>
                <h1>Clean headline</h1>
                <p>Share</p>
                <p>The first real paragraph has more than six words in it.</p>
                <p>Second paragraph: prices rose 5% &amp; wages didn&#39;t keep up.</p>
              </article>
              <aside>Related: another story you might like to read today</aside>
            </body></html>
            """;

        var page = HtmlTextExtractor.Extract(html);

        Assert.Equal("Clean headline", page.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), page.PublishedAt);
        Assert.Equal(
            "The first real paragraph has more than six words in it.\n\nSecond paragraph: prices rose 5% & wages didn't keep up.",
            page.Text);
    }

    [Fact]
    public void Extract_NoArticleTag_FallsBackToMainThenBody()
    {
        const string html = "<html><body><main><p>Main content paragraph with enough words to keep.</p></main></body></html>";

        Assert.Equal("Main content paragraph with enough words to keep.", HtmlTextExtractor.Extract(html).Text);
    }

    [Fact]
    public void Extract_TitleFallsBackToTitleTag()
    {
        Assert.Equal("Only a title", HtmlTextExtractor.Extract("<html><head><title> Only a title </title></head></html>").Title);
    }
}

public class UrlIdentityTests
{
    [Theory]
    [InlineData("https://WWW.BBC.co.uk/news/1?utm_source=x&id=7#top", "https://www.bbc.co.uk/news/1?id=7")]
    [InlineData("https://news.un.org/story/", "https://news.un.org/story")]
    [InlineData("https://news.un.org:443/story", "https://news.un.org/story")]
    [InlineData("https://news.un.org/", "https://news.un.org/")]
    public void Normalize_RemovesNoise(string input, string expected)
    {
        Assert.Equal(expected, UrlIdentity.Normalize(input));
    }

    [Fact]
    public void DocumentId_SameForEquivalentUrls_DifferentForDifferentPages()
    {
        var a = UrlIdentity.DocumentId("https://news.un.org/story/1?utm_campaign=rss");
        var b = UrlIdentity.DocumentId("https://NEWS.un.org/story/1/#comments");
        var c = UrlIdentity.DocumentId("https://news.un.org/story/2");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("doc-", a, StringComparison.Ordinal);
        Assert.Equal(36, a.Length);
    }
}

public class RobotsTxtTests
{
    private const string Robots = """
        # comment
        User-agent: *
        Disallow: /private/
        Disallow: /*.pdf$
        Allow: /private/press/

        User-agent: BackstoryBot
        User-agent: OtherBot
        Disallow: /no-bots/
        """;

    [Theory]
    [InlineData("SomeCrawler", "/news/1", true)]
    [InlineData("SomeCrawler", "/private/memo", false)]
    [InlineData("SomeCrawler", "/private/press/release", true)]   // longer Allow wins
    [InlineData("SomeCrawler", "/files/report.pdf", false)]       // wildcard + $
    [InlineData("SomeCrawler", "/files/report.pdf?v=2", true)]    // $ means end of path
    [InlineData("BackstoryBot/0.1", "/private/memo", true)]       // our own group replaces "*"
    [InlineData("BackstoryBot/0.1", "/no-bots/x", false)]
    public void IsAllowed_FollowsGroupAndLongestMatch(string agent, string path, bool expected)
    {
        Assert.Equal(expected, RobotsTxt.Parse(Robots, agent).IsAllowed(path));
    }

    [Fact]
    public void EmptyDisallow_AllowsEverything()
    {
        Assert.True(RobotsTxt.Parse("User-agent: *\nDisallow:", "x").IsAllowed("/anything"));
    }
}

public class DomainRateLimiterTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task SameHost_WaitsInterval_OtherHostDoesNot()
    {
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();
        var limiter = new DomainRateLimiter(clock, (d, _) => { waits.Add(d); return Task.CompletedTask; });

        await limiter.WaitAsync("news.un.org", maxRequestsPerMinute: 30, default); // first: no wait
        await limiter.WaitAsync("news.un.org", 30, default);                         // second: 2 s
        await limiter.WaitAsync("www.bbc.co.uk", 30, default);                       // other host: no wait

        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, waits);
    }
}

public class SeenUrlCacheTests
{
    [Fact]
    public void ForgetsOldestBeyondCapacity()
    {
        var cache = new SeenUrlCache(capacity: 2);
        cache.Add("a");
        cache.Add("b");
        cache.Add("c");

        Assert.False(cache.Contains("a"));
        Assert.True(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
        Assert.Equal(2, cache.Count);
    }
}

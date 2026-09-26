using System.Net;
using Backstory.Core.Ingestion;
using Backstory.Core.Trust;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backstory.Core.Tests.Ingestion;

/// <summary>Each test simulates one checkpoint from the step 4 design firing.</summary>
public class SafeFetcherTests
{
    private static readonly TrustRegistry Trust = new(
    [
        new TrustedSource("un.org", "United Nations", TrustTier.Primary),
        new TrustedSource("bbc.co.uk", "BBC", TrustTier.Wire),
        new TrustedSource("bbc.com", "BBC", TrustTier.Wire),
    ]);

    private const string Article = "<html><body><article><p>This is a long enough paragraph of article text for the test.</p></article></body></html>";

    private readonly FakeHttp _web = new();

    private SafeFetcher CreateFetcher(IngestionOptions? options = null) => new(
        new HttpClient(_web),
        Trust,
        new DomainRateLimiter(TimeProvider.System, (_, _) => Task.CompletedTask), // no real waiting in tests
        options ?? new IngestionOptions(),
        TimeProvider.System,
        NullLogger<SafeFetcher>.Instance);

    [Fact]
    public async Task TrustedPage_IsFetched()
    {
        _web.Html("https://news.un.org/en/story/1", Article);

        var result = await CreateFetcher().FetchAsync("https://news.un.org/en/story/1", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Fetched, result.Status);
        Assert.Equal("un.org", result.Source!.Domain);
        Assert.Contains("long enough paragraph", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C2_UntrustedUrl_IsRejected_AndNeverRequested()
    {
        var result = await CreateFetcher().FetchAsync("https://evil-news.com/story", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
        Assert.Contains("untrusted URL", result.Reason, StringComparison.Ordinal);
        Assert.Empty(_web.Requested); // not even robots.txt
    }

    [Fact]
    public async Task C3_RedirectToUntrustedHost_IsRejected_AndTargetNeverRequested()
    {
        _web.Redirect("https://www.bbc.co.uk/news/1", "https://evil.com/phish");

        var result = await CreateFetcher().FetchAsync("https://www.bbc.co.uk/news/1", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
        Assert.Contains("redirect 1 to untrusted URL https://evil.com/phish", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(_web.Requested, u => u.Contains("evil.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C3_RedirectBetweenTrustedHosts_IsFollowed()
    {
        _web.Redirect("https://www.bbc.co.uk/news/1", "https://www.bbc.com/news/1", HttpStatusCode.MovedPermanently)
            .Html("https://www.bbc.com/news/1", Article);

        var result = await CreateFetcher().FetchAsync("https://www.bbc.co.uk/news/1", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Fetched, result.Status);
        Assert.Equal("https://www.bbc.com/news/1", result.FinalUrl);
        Assert.Equal("bbc.com", result.Source!.Domain);
    }

    [Fact]
    public async Task C3_RelativeRedirect_IsResolvedAgainstCurrentUrl()
    {
        _web.Redirect("https://news.un.org/old", "/new").Html("https://news.un.org/new", Article);

        var result = await CreateFetcher().FetchAsync("https://news.un.org/old", FetchKind.Page, default);

        Assert.Equal("https://news.un.org/new", result.FinalUrl);
    }

    [Fact]
    public async Task C3_RedirectLoop_StopsAtMaxRedirects()
    {
        _web.Redirect("https://news.un.org/a", "https://news.un.org/b")
            .Redirect("https://news.un.org/b", "https://news.un.org/a");

        var result = await CreateFetcher(new IngestionOptions { MaxRedirects = 3 })
            .FetchAsync("https://news.un.org/a", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
        Assert.Contains("more than 3 redirects", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C4_NonHtmlPage_IsRejected()
    {
        _web.Text("https://news.un.org/report.pdf", "%PDF-1.7", "application/pdf");

        var result = await CreateFetcher().FetchAsync("https://news.un.org/report.pdf", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
        Assert.Contains("application/pdf", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C4_OversizedBody_IsRejected()
    {
        _web.Html("https://news.un.org/huge", "<p>" + new string('x', 5000) + "</p>");

        var result = await CreateFetcher(new IngestionOptions { MaxDocumentBytes = 1000 })
            .FetchAsync("https://news.un.org/huge", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
        Assert.Contains("too large", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RobotsTxtDisallow_IsRejected_AndPageNeverRequested()
    {
        _web.Text("https://news.un.org/robots.txt", "User-agent: *\nDisallow: /private/")
            .Html("https://news.un.org/private/memo", Article);

        var result = await CreateFetcher().FetchAsync("https://news.un.org/private/memo", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
        Assert.Contains("robots.txt", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("https://news.un.org/private/memo", _web.Requested);
    }

    [Fact]
    public async Task MissingRobotsTxt_MeansAllowed()
    {
        _web.Html("https://news.un.org/story", Article); // robots.txt not registered → 404

        var result = await CreateFetcher().FetchAsync("https://news.un.org/story", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Fetched, result.Status);
    }

    [Fact]
    public async Task RobotsTxtServerError_MeansStayAway()
    {
        _web.Status("https://news.un.org/robots.txt", HttpStatusCode.ServiceUnavailable)
            .Html("https://news.un.org/story", Article);

        var result = await CreateFetcher().FetchAsync("https://news.un.org/story", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task ServerError_IsFailed_NotRejected_SoItIsRetriedLater()
    {
        _web.Status("https://news.un.org/story", HttpStatusCode.BadGateway);

        var result = await CreateFetcher().FetchAsync("https://news.un.org/story", FetchKind.Page, default);

        Assert.Equal(FetchStatus.Failed, result.Status);
        Assert.Equal(502, result.HttpStatus);
    }

    [Fact]
    public async Task ServerStallsMidBody_TimesOut_InsteadOfHangingForever()
    {
        // Regression: the timeout used to cover only the response headers, so a server that sent
        // headers and then stalled could block a feed's poll forever.
        _web.Add("https://news.un.org/slow", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()) { Headers = { ContentType = new("text/html") } },
        });

        var result = await CreateFetcher(new IngestionOptions { RequestTimeout = TimeSpan.FromMilliseconds(300) })
            .FetchAsync("https://news.un.org/slow", FetchKind.Page, default)
            .WaitAsync(TimeSpan.FromSeconds(10)); // if the fix regresses, fail instead of hanging the test run

        Assert.Equal(FetchStatus.Failed, result.Status);
        Assert.Contains("timed out reading the body", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>A response body that never delivers a byte.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task BodyWithByteOrderMark_IsDecodedWithoutIt()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("<rss></rss>")).ToArray();
        _web.Add("https://news.un.org/feed", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/rss+xml") } },
        });

        var result = await CreateFetcher().FetchAsync("https://news.un.org/feed", FetchKind.Feed, default);

        Assert.Equal("<rss></rss>", result.Body); // no invisible U+FEFF at the start
    }

    [Fact]
    public async Task Feed_RequiresXmlContentType()
    {
        _web.Html("https://news.un.org/feed", "<html>not a feed</html>");

        var result = await CreateFetcher().FetchAsync("https://news.un.org/feed", FetchKind.Feed, default);

        Assert.Equal(FetchStatus.Rejected, result.Status);
    }
}

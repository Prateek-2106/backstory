using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Backstory.Core.Trust;
using Microsoft.Extensions.Logging;

namespace Backstory.Core.Ingestion;

public enum FetchKind
{
    /// <summary>An article page: must be HTML, subject to robots.txt.</summary>
    Page,

    /// <summary>An RSS/Atom feed: must be XML.</summary>
    Feed,

    /// <summary>A site's robots.txt: plain text.</summary>
    Robots,
}

public enum FetchStatus
{
    /// <summary>Got the content.</summary>
    Fetched,

    /// <summary>Our policy said no (untrusted, robots.txt, wrong type, too big). Retrying won't help.</summary>
    Rejected,

    /// <summary>Network or HTTP error. Worth retrying on the next poll.</summary>
    Failed,
}

public sealed record FetchResult(
    FetchStatus Status,
    string RequestedUrl,
    string? FinalUrl,
    TrustedSource? Source,
    string? Body,
    string Reason,
    int? HttpStatus = null)
{
    public bool IsFetched => Status == FetchStatus.Fetched;
}

/// <summary>
/// The only way ingestion reaches the internet. Every request goes through the same gates:
///
///   for each hop (original URL, then each redirect target):
///     C2/C3  trust.Evaluate(url)          not trusted  → Rejected, nothing is requested
///            robots.txt (pages only)      disallowed   → Rejected
///            per-domain rate limit        waits, never skips
///            GET  (auto-redirect is OFF, so every redirect comes back here to be checked)
///   C4  content-type and size checks                    → Rejected
///
/// The HttpClient passed in MUST have AllowAutoRedirect = false (the worker's Program.cs sets it).
/// </summary>
public sealed class SafeFetcher
{
    private readonly HttpClient _http;
    private readonly TrustRegistry _trust;
    private readonly DomainRateLimiter _limiter;
    private readonly IngestionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SafeFetcher> _logger;
    private readonly ConcurrentDictionary<string, (RobotsTxt Rules, DateTimeOffset Expires)> _robots = new(StringComparer.OrdinalIgnoreCase);

    public SafeFetcher(HttpClient http, TrustRegistry trust, DomainRateLimiter limiter, IngestionOptions options,
        TimeProvider time, ILogger<SafeFetcher> logger)
    {
        _http = http;
        _trust = trust;
        _limiter = limiter;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public Task<FetchResult> FetchAsync(string url, FetchKind kind, CancellationToken ct) =>
        FetchCoreAsync(url, kind, checkRobots: kind == FetchKind.Page, enforceTrust: true, ct);

    /// <summary>
    /// For the newsroom feed only: our own headlines, which are not "evidence" and so not on the allowlist.
    /// Every other gate (redirect limit, content type, size, rate limit) still applies.
    /// </summary>
    public Task<FetchResult> FetchUntrustedFeedAsync(string url, CancellationToken ct) =>
        FetchCoreAsync(url, FetchKind.Feed, checkRobots: false, enforceTrust: false, ct);

    private const int DefaultRequestsPerMinute = 30;

    private async Task<FetchResult> FetchCoreAsync(string url, FetchKind kind, bool checkRobots, bool enforceTrust, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop <= _options.MaxRedirects; hop++)
        {
            TrustedSource? source = null;
            if (enforceTrust)
            {
                // C2 (first hop) / C3 (every redirect): is this URL trusted?
                var decision = _trust.Evaluate(current);
                if (!decision.IsTrusted)
                    return Reject(url, current, hop == 0 ? $"untrusted URL: {decision.Reason}" : $"redirect {hop} to untrusted URL {current}: {decision.Reason}");
                source = decision.Source;
            }

            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                return Reject(url, current, "not an http(s) URL");

            if (checkRobots && !await IsAllowedByRobotsAsync(uri, ct))
                return Reject(url, current, $"robots.txt on {uri.Host} disallows {uri.PathAndQuery}");

            await _limiter.WaitAsync(uri.Host, source?.MaxRequestsPerMinute ?? DefaultRequestsPerMinute, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd(kind switch
            {
                FetchKind.Feed => "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8",
                FetchKind.Robots => "text/plain",
                _ => "text/html, application/xhtml+xml;q=0.9",
            });

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                return Fail(url, current, $"network error: {ex.Message}");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail(url, current, "timed out");
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status is >= 300 and < 400)
                {
                    if (response.Headers.Location is not { } location)
                        return Fail(url, current, $"HTTP {status} without a Location header", status);
                    current = new Uri(uri, location).AbsoluteUri; // Location may be relative
                    _logger.LogDebug("Redirect {Hop}: {From} -> {To}", hop + 1, uri, current);
                    continue; // back to the top: the new URL gets trust-checked like the first one
                }

                if (!response.IsSuccessStatusCode)
                    return Fail(url, current, $"HTTP {status}", status);

                // C4: is it the kind of thing we asked for?
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (!IsAcceptable(kind, mediaType))
                    return Reject(url, current, $"content-type '{mediaType}' is not {(kind == FetchKind.Feed ? "a feed" : "HTML")}");

                if (response.Content.Headers.ContentLength > _options.MaxDocumentBytes)
                    return Reject(url, current, $"too large ({response.Content.Headers.ContentLength} bytes)");

                var body = await ReadCappedAsync(response.Content, ct);
                if (body is null)
                    return Reject(url, current, $"too large (over {_options.MaxDocumentBytes} bytes)");

                return new FetchResult(FetchStatus.Fetched, url, current, source, body, "ok", status);
            }
        }

        return Reject(url, current, $"more than {_options.MaxRedirects} redirects");
    }

    private static bool IsAcceptable(FetchKind kind, string mediaType) => kind switch
    {
        FetchKind.Page => mediaType is "text/html" or "application/xhtml+xml",
        FetchKind.Feed => mediaType.Contains("xml", StringComparison.Ordinal) || mediaType.Contains("rss", StringComparison.Ordinal) || mediaType.Contains("atom", StringComparison.Ordinal),
        FetchKind.Robots => true,
        _ => false,
    };

    /// <summary>Reads at most MaxDocumentBytes (servers can lie about or omit Content-Length). Null = too big.</summary>
    private async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _options.MaxDocumentBytes)
                return null;
        }

        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* unknown charset: stay with UTF-8 */ }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task<bool> IsAllowedByRobotsAsync(Uri uri, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!_robots.TryGetValue(uri.Host, out var cached) || cached.Expires <= now)
        {
            cached = (await LoadRobotsAsync(uri, ct), now + _options.RobotsCacheDuration);
            _robots[uri.Host] = cached;
        }
        return cached.Rules.IsAllowed(uri.PathAndQuery);
    }

    private async Task<RobotsTxt> LoadRobotsAsync(Uri uri, CancellationToken ct)
    {
        // Same checked loop as everything else, so a robots.txt redirect (bbc.co.uk -> bbc.com) is
        // followed only to trusted hosts.
        var result = await FetchCoreAsync($"{uri.Scheme}://{uri.Authority}/robots.txt", FetchKind.Robots, checkRobots: false, enforceTrust: true, ct);

        if (result.IsFetched)
            return RobotsTxt.Parse(result.Body!, _options.RobotsToken);

        // No robots.txt (404 etc.) = no rules.
        if (result.HttpStatus is >= 400 and < 500)
            return RobotsTxt.AllowAll;

        // Server errors, timeouts, redirects off the allowlist: be conservative and stay away for now.
        _logger.LogWarning("robots.txt for {Host} unavailable ({Reason}); treating the site as off-limits until the cache expires",
            uri.Host, result.Reason);
        return DisallowAll;
    }

    private RobotsTxt DisallowAll => RobotsTxt.Parse("User-agent: *\nDisallow: /", _options.RobotsToken);

    private FetchResult Reject(string requested, string current, string reason)
    {
        _logger.LogInformation("Rejected {Url}: {Reason}", requested, reason);
        return new FetchResult(FetchStatus.Rejected, requested, current, null, null, reason);
    }

    private FetchResult Fail(string requested, string current, string reason, int? status = null)
    {
        _logger.LogWarning("Failed {Url}: {Reason}", requested, reason);
        return new FetchResult(FetchStatus.Failed, requested, current, null, null, reason, status);
    }
}

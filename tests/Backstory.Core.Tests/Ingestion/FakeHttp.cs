using System.Net;
using System.Text;

namespace Backstory.Core.Tests.Ingestion;

/// <summary>
/// A pretend internet. Register URL → response; every request is recorded, so tests can prove
/// that an untrusted URL was never even contacted. Unknown URLs return 404.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public FakeHttp Html(string url, string html) => Add(url, () => Content(html, "text/html"));

    public FakeHttp Xml(string url, string xml) => Add(url, () => Content(xml, "application/rss+xml"));

    public FakeHttp Text(string url, string text, string mediaType = "text/plain") => Add(url, () => Content(text, mediaType));

    public FakeHttp Redirect(string url, string location, HttpStatusCode code = HttpStatusCode.Found) =>
        Add(url, () => new HttpResponseMessage(code) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } });

    public FakeHttp Status(string url, HttpStatusCode code) => Add(url, () => new HttpResponseMessage(code));

    public FakeHttp Add(string url, Func<HttpResponseMessage> response)
    {
        _routes[url] = response;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requested.Add(url);
        return Task.FromResult(_routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Content(string body, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}

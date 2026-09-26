using System.Security.Cryptography;
using System.Text;

namespace Backstory.Core.Ingestion;

/// <summary>
/// Stable IDs from URLs. The same page reached via different-looking URLs
/// (tracking parameters, #fragments, trailing slashes, upper-case host) gets the same ID,
/// so re-fetching it overwrites the old copy instead of creating a duplicate.
/// </summary>
public static class UrlIdentity
{
    private static readonly string[] TrackingPrefixes = ["utm_", "fbclid", "gclid", "ocid", "cmpid", "at_medium", "at_campaign", "mc_"];

    public static string Normalize(string url)
    {
        var uri = new Uri(url);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Fragment = "",
            Query = RemoveTracking(uri.Query),
        };

        var path = builder.Path;
        if (path.Length > 1 && path.EndsWith('/'))
            builder.Path = path.TrimEnd('/');

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>"doc-" + 32 hex chars. Key for sources.documents.v1 and the vector store.</summary>
    public static string DocumentId(string url) => "doc-" + Hash(Normalize(url));

    /// <summary>"art-" + 32 hex chars. Key for news.articles.v1.</summary>
    public static string ArticleId(string url) => "art-" + Hash(Normalize(url));

    private static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32];

    private static string RemoveTracking(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?") return "";
        var kept = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !TrackingPrefixes.Any(t => pair.StartsWith(t, StringComparison.OrdinalIgnoreCase)));
        return string.Join('&', kept);
    }
}

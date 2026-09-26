using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backstory.Core.Trust;

/// <summary>
/// The allowlist of sources we are willing to fetch, index and cite.
/// Anything not listed here is rejected. The design doc enforces this at four points
/// (ingestion, indexing, retrieval, verification); all four call <see cref="Evaluate(string)"/>.
/// </summary>
public sealed class TrustRegistry
{
    private readonly IReadOnlyList<TrustedSource> _sources;

    public TrustRegistry(IEnumerable<TrustedSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = Validate(sources.ToList());
    }

    public IReadOnlyList<TrustedSource> Sources => _sources;

    /// <summary>Decide whether a URL comes from a trusted source, and which one.</summary>
    public TrustDecision Evaluate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return TrustDecision.Rejected(null, "empty URL");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return TrustDecision.Rejected(null, "not an absolute URL");

        return Evaluate(uri);
    }

    public TrustDecision Evaluate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (uri.Scheme != Uri.UriSchemeHttps)
            return TrustDecision.Rejected(uri.Host, $"scheme '{uri.Scheme}' not allowed (https only)");

        // "https://bbc.co.uk@evil.com" has host evil.com; reject anything with credentials outright.
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return TrustDecision.Rejected(uri.Host, "URL contains user info");

        if (uri.HostNameType != UriHostNameType.Dns)
            return TrustDecision.Rejected(uri.Host, "host is an IP address, not a domain");

        // IdnHost turns unicode look-alike domains into punycode (xn--...), so they cannot impersonate a listed domain.
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

        // Most specific entry wins: "news.un.org" beats "un.org" beats "*.org".
        TrustedSource? best = null;
        foreach (var source in _sources)
        {
            if (Matches(host, source) && (best is null || source.Suffix.Length > best.Suffix.Length))
                best = source;
        }

        return best is null
            ? TrustDecision.Rejected(host, "domain not on the allowlist")
            : TrustDecision.Trusted(host, best);
    }

    public bool IsTrusted(string url) => Evaluate(url).IsTrusted;

    private static bool Matches(string host, TrustedSource source)
    {
        var suffix = source.Suffix;
        if (source.IsWildcard)
            return host.EndsWith("." + suffix, StringComparison.Ordinal);

        // Exact domain or a real subdomain. The "." check stops "evilapnews.com" matching "apnews.com".
        return host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal);
    }

    private static List<TrustedSource> Validate(List<TrustedSource> sources)
    {
        if (sources.Count == 0)
            throw new InvalidOperationException("Trust registry is empty; refusing to start with no trusted sources.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sources)
        {
            if (string.IsNullOrWhiteSpace(s.Domain))
                throw new InvalidOperationException("A trusted source has an empty domain.");
            if (s.Domain != s.Domain.Trim().ToLowerInvariant())
                throw new InvalidOperationException($"Domain '{s.Domain}' must be lowercase with no spaces.");
            if (s.Domain.Contains("://", StringComparison.Ordinal) || s.Domain.Contains('/', StringComparison.Ordinal))
                throw new InvalidOperationException($"Domain '{s.Domain}' must be a bare domain, not a URL.");
            if (s.Domain.IndexOf('*', StringComparison.Ordinal) is var star && star >= 0 && (star != 0 || !s.IsWildcard))
                throw new InvalidOperationException($"Domain '{s.Domain}': '*' is only allowed as a leading '*.'.");
            if (!Enum.IsDefined(s.Tier))
                throw new InvalidOperationException($"Domain '{s.Domain}' has unknown tier {(int)s.Tier}.");
            if (!seen.Add(s.Domain))
                throw new InvalidOperationException($"Domain '{s.Domain}' is listed twice.");
        }

        return sources;
    }

    // ---------- Loading from JSON ----------

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record RegistryFile(List<TrustedSource>? Sources);

    public static TrustRegistry FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions)
                   ?? throw new InvalidOperationException("Trust registry JSON was null.");
        return new TrustRegistry(file.Sources ?? []);
    }

    public static TrustRegistry FromFile(string path) => FromJson(File.ReadAllText(path));
}

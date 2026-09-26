namespace Backstory.Core.Trust;

/// <summary>One allowlist entry from trusted-sources.json.</summary>
/// <param name="Domain">
/// "apnews.com" matches apnews.com and any subdomain (www.apnews.com).
/// "*.gov" matches every host ending in ".gov" (whitehouse.gov, www.cdc.gov).
/// </param>
/// <param name="Name">Human-readable name shown to readers in the sources list.</param>
/// <param name="AllowFullText">False = we may cite and link it but not store its full text (licensing).</param>
/// <param name="MaxRequestsPerMinute">Politeness limit for the fetcher.</param>
public sealed record TrustedSource(
    string Domain,
    string Name,
    TrustTier Tier,
    bool AllowFullText = true,
    int MaxRequestsPerMinute = 30)
{
    public bool IsWildcard => Domain.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>The part after "*." for wildcards, otherwise the domain itself.</summary>
    internal string Suffix => IsWildcard ? Domain[2..] : Domain;
}

/// <summary>The answer to "may we use this URL?". Always carries a reason, so logs explain every rejection.</summary>
public sealed record TrustDecision(bool IsTrusted, string? Host, TrustedSource? Source, string Reason)
{
    public static TrustDecision Trusted(string host, TrustedSource source) =>
        new(true, host, source, $"matched '{source.Domain}' (tier {(int)source.Tier})");

    public static TrustDecision Rejected(string? host, string reason) => new(false, host, null, reason);
}

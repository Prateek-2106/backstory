using Backstory.Core.Trust;

namespace Backstory.Core.Tests.Trust;

public class TrustRegistryTests
{
    // A small registry built in code, so each test's expectations are visible right here.
    private static readonly TrustRegistry Registry = new(
    [
        new TrustedSource("*.gov", "U.S. government", TrustTier.Primary),
        new TrustedSource("supremecourt.gov", "U.S. Supreme Court", TrustTier.Primary),
        new TrustedSource("apnews.com", "Associated Press", TrustTier.Wire, AllowFullText: false),
        new TrustedSource("bbc.co.uk", "BBC", TrustTier.Wire),
        new TrustedSource("en.wikipedia.org", "Wikipedia", TrustTier.Reference),
    ]);

    [Theory]
    [InlineData("https://apnews.com/article/abc", "apnews.com", TrustTier.Wire)]
    [InlineData("https://www.apnews.com/article/abc", "apnews.com", TrustTier.Wire)]          // subdomain
    [InlineData("https://news.bbc.co.uk/2/hi/world", "bbc.co.uk", TrustTier.Wire)]            // deeper subdomain
    [InlineData("https://APNEWS.COM/Article", "apnews.com", TrustTier.Wire)]                  // case-insensitive host
    [InlineData("https://www.cdc.gov/flu/", "*.gov", TrustTier.Primary)]                       // wildcard
    [InlineData("https://www.supremecourt.gov/opinions", "supremecourt.gov", TrustTier.Primary)] // specific beats wildcard
    [InlineData("https://en.wikipedia.org/wiki/NATO", "en.wikipedia.org", TrustTier.Reference)]
    public void Evaluate_AllowlistedUrl_IsTrustedWithMatchingEntry(string url, string expectedDomain, TrustTier expectedTier)
    {
        var decision = Registry.Evaluate(url);

        Assert.True(decision.IsTrusted, decision.Reason);
        Assert.Equal(expectedDomain, decision.Source!.Domain);
        Assert.Equal(expectedTier, decision.Source.Tier);
    }

    [Theory]
    [InlineData("https://evil-news.com/story", "not on the allowlist")]
    [InlineData("https://evilapnews.com/story", "not on the allowlist")]          // suffix without a dot
    [InlineData("https://apnews.com.evil.com/story", "not on the allowlist")]     // listed domain as a prefix
    [InlineData("https://fr.wikipedia.org/wiki/OTAN", "not on the allowlist")]    // only en.wikipedia is listed
    [InlineData("https://gov/", "not on the allowlist")]                          // "*.gov" needs something before .gov
    [InlineData("http://apnews.com/article/abc", "https only")]
    [InlineData("ftp://apnews.com/file", "https only")]
    [InlineData("https://apnews.com@evil.com/", "user info")]                     // host is really evil.com
    [InlineData("https://93.184.216.34/", "IP address")]
    [InlineData("apnews.com/article", "not an absolute URL")]
    [InlineData("", "empty URL")]
    public void Evaluate_UntrustedUrl_IsRejectedWithReason(string url, string expectedReason)
    {
        var decision = Registry.Evaluate(url);

        Assert.False(decision.IsTrusted);
        Assert.Null(decision.Source);
        Assert.Contains(expectedReason, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UnicodeLookalikeDomain_IsRejected()
    {
        // "аpnews.com" with a Cyrillic 'а' becomes punycode (xn--...) and must not match apnews.com.
        var decision = Registry.Evaluate("https://аpnews.com/article");

        Assert.False(decision.IsTrusted);
        Assert.StartsWith("xn--", decision.Host, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("APNEWS.COM", "lowercase")]
    [InlineData("https://apnews.com", "bare domain")]
    [InlineData("apnews.com/news", "bare domain")]
    [InlineData("ap*.com", "leading '*.'")]
    [InlineData("", "empty domain")]
    public void Constructor_InvalidDomain_Throws(string domain, string expectedMessage)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new TrustRegistry([new TrustedSource(domain, "x", TrustTier.Wire)]));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DuplicateDomain_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new TrustRegistry(
        [
            new TrustedSource("apnews.com", "AP", TrustTier.Wire),
            new TrustedSource("apnews.com", "AP again", TrustTier.Wire),
        ]));

        Assert.Contains("listed twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_EmptyList_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new TrustRegistry([]));
    }

    [Fact]
    public void FromJson_ParsesTierNamesAndDefaults()
    {
        const string json = """
            // comments are allowed
            { "sources": [
                { "domain": "apnews.com", "name": "AP", "tier": "wire", "allowFullText": false },
                { "domain": "un.org", "name": "UN", "tier": "primary" },
            ] }
            """;

        var registry = TrustRegistry.FromJson(json);

        var ap = registry.Evaluate("https://apnews.com/x").Source!;
        Assert.Equal(TrustTier.Wire, ap.Tier);
        Assert.False(ap.AllowFullText);

        var un = registry.Evaluate("https://news.un.org/x").Source!;
        Assert.True(un.AllowFullText);            // default
        Assert.Equal(30, un.MaxRequestsPerMinute); // default
    }

    [Fact]
    public void FromFile_ShippedConfig_LoadsAndCoversEveryTier()
    {
        // config/trusted-sources.json is copied next to the test DLL (see the .csproj).
        var path = Path.Combine(AppContext.BaseDirectory, "trusted-sources.json");
        var registry = TrustRegistry.FromFile(path);

        Assert.True(registry.Sources.Count >= 10);
        Assert.Contains(registry.Sources, s => s.Tier == TrustTier.Primary);
        Assert.Contains(registry.Sources, s => s.Tier == TrustTier.Wire);
        Assert.Contains(registry.Sources, s => s.Tier == TrustTier.Reference);
        Assert.True(registry.IsTrusted("https://www.bbc.co.uk/news/world"));
        Assert.False(registry.IsTrusted("https://example.com/"));
    }

    [Fact]
    public void Weight_HigherTrust_GetsHigherWeight()
    {
        Assert.True(TrustTier.Primary.Weight() > TrustTier.Wire.Weight());
        Assert.True(TrustTier.Wire.Weight() > TrustTier.Reference.Weight());
    }
}

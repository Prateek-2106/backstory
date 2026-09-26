namespace Backstory.Core.Trust;

/// <summary>How much we trust a source. Lower number = more authoritative.</summary>
public enum TrustTier
{
    /// <summary>Primary and official: governments, courts, UN, central banks.</summary>
    Primary = 1,

    /// <summary>Wire services and public broadcasters: AP, Reuters, BBC, NPR.</summary>
    Wire = 2,

    /// <summary>Reference works: Wikipedia, Britannica.</summary>
    Reference = 3,
}

public static class TrustTierExtensions
{
    /// <summary>Multiplier applied to a chunk's similarity score at retrieval time (design doc: ranking score).</summary>
    public static double Weight(this TrustTier tier) => tier switch
    {
        TrustTier.Primary => 1.00,
        TrustTier.Wire => 0.95,
        TrustTier.Reference => 0.85,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown trust tier."),
    };
}

namespace Backstory.Core.Ingestion;

/// <summary>Bound from the "Ingestion" config section.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Identifies us to websites (and to robots.txt rules). Wikipedia requires a descriptive one.</summary>
    public string UserAgent { get; set; } = "BackstoryBot/0.1 (+https://github.com/Prateek-2106/backstory)";

    /// <summary>The token matched against "User-agent:" lines in robots.txt.</summary>
    public string RobotsToken { get; set; } = "BackstoryBot";

    public string TrustedSourcesPath { get; set; } = "trusted-sources.json";
    public int MaxDocumentBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxRedirects { get; set; } = 5;
    public int MaxItemsPerPoll { get; set; } = 30;

    /// <summary>Extracted page text shorter than this falls back to the feed summary.</summary>
    public int MinTextLength { get; set; } = 200;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan RobotsCacheDuration { get; set; } = TimeSpan.FromHours(1);

    public List<FeedDefinition> Feeds { get; set; } = [];
}

public enum FeedKind
{
    /// <summary>Trusted background sources: items become SourceDocumentFetched (RAG evidence).</summary>
    Source,

    /// <summary>Our own headlines: items become ArticlePublished (what readers get background for).</summary>
    Newsroom,
}

public sealed class FeedDefinition
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public FeedKind Kind { get; set; } = FeedKind.Source;
    public string? Section { get; set; }
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);
    public bool Enabled { get; set; } = true;
}

namespace Backstory.Core.Contracts;

// The payloads that travel inside an EventEnvelope. They are immutable records:
// once an event is published it is a fact, so nothing should mutate it.

/// <summary>A newsroom article was published or updated (CMS webhook or feed poller).</summary>
public sealed record ArticlePublished(
    string ArticleId,
    string Headline,
    string? Summary,
    string? Body,
    string? Url,
    string? Section,
    DateTimeOffset PublishedAt);

/// <summary>A document fetched from a trusted source, ready to be chunked and embedded.</summary>
/// <param name="DocumentId">Stable ID derived from the URL, so refetching the same page overwrites it.</param>
/// <param name="Origin">What fetched it: "rss", "wikipedia", "on-demand".</param>
public sealed record SourceDocumentFetched(
    string DocumentId,
    string Url,
    string Domain,
    string Title,
    string Text,
    DateTimeOffset? PublishedAt,
    DateTimeOffset FetchedAt,
    string Origin);

/// <summary>A document's chunks were embedded and written to the vector store.</summary>
public sealed record SourceIndexed(
    string DocumentId,
    string Domain,
    int ChunkCount,
    DateTimeOffset IndexedAt);

/// <summary>Explicit request to generate background, e.g. a cache miss or an editor pressing "regenerate".</summary>
/// <param name="Force">True = generate a new version even if one exists for the same content.</param>
public sealed record ContextRequested(
    string ArticleId,
    string Reason,
    bool Force);

/// <summary>A brief was generated. Status is "ready" or "insufficient_evidence".</summary>
public sealed record ContextGenerated(
    string ArticleId,
    string Status,
    int Version,
    int CitationCount,
    DateTimeOffset GeneratedAt);

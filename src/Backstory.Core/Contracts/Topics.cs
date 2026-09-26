namespace Backstory.Core.Contracts;

/// <summary>
/// Kafka topic names. The ".v1" suffix is the schema version: a breaking change to an
/// event ships as a new topic (".v2") so old and new consumers can run side by side.
/// </summary>
public static class Topics
{
    /// <summary>Newsroom articles published or updated. Key: articleId.</summary>
    public const string Articles = "news.articles.v1";

    /// <summary>Documents fetched from trusted sources, waiting to be indexed. Key: documentId.</summary>
    public const string SourceDocuments = "sources.documents.v1";

    /// <summary>Confirmation that a document's chunks are in the vector store. Key: documentId.</summary>
    public const string SourcesIndexed = "sources.indexed.v1";

    /// <summary>Explicit request to (re)generate background for an article. Key: articleId.</summary>
    public const string ContextRequests = "context.requests.v1";

    /// <summary>A background brief was generated and stored. Key: articleId.</summary>
    public const string ContextGenerated = "context.generated.v1";

    public static readonly IReadOnlyList<string> All =
        [Articles, SourceDocuments, SourcesIndexed, ContextRequests, ContextGenerated];

    /// <summary>Messages that failed every retry are parked here with error details.</summary>
    public static string DeadLetter(string topic) => topic + ".dlq";
}

namespace Backstory.Core.Indexing;

/// <summary>Bound from the "Indexing" config section.</summary>
public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    public string TrustedSourcesPath { get; set; } = "trusted-sources.json";

    // ---- embeddings (Ollama)
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public int EmbeddingDimensions { get; set; } = 768;
    public int EmbeddingBatchSize { get; set; } = 16;

    /// <summary>nomic-embed-text is trained with these task prefixes; other models use empty strings.</summary>
    public string DocumentPrefix { get; set; } = "search_document: ";
    public string QueryPrefix { get; set; } = "search_query: ";

    /// <summary>First call loads the model into GPU memory, which can take a while.</summary>
    public TimeSpan EmbeddingTimeout { get; set; } = TimeSpan.FromMinutes(2);

    // ---- vector store (Qdrant)
    public string QdrantUrl { get; set; } = "http://localhost:6333";
    public string Collection { get; set; } = "trusted_chunks";

    // ---- chunking
    public int ChunkWords { get; set; } = 250;
    public int OverlapWords { get; set; } = 40;
    public int MinChunkWords { get; set; } = 40;
}

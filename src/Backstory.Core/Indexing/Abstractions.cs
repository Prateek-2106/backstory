using Backstory.Core.Trust;

namespace Backstory.Core.Indexing;

/// <summary>Documents and queries are embedded differently by some models (nomic uses prefixes).</summary>
public enum EmbeddingPurpose
{
    Document,
    Query,
}

/// <summary>Turns text into vectors. Implemented by OllamaEmbeddingProvider; Claude/OpenAI/Bedrock can be added later.</summary>
public interface IEmbeddingProvider
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct);
}

/// <summary>Everything we store about one chunk. Becomes the Qdrant point payload.</summary>
public sealed record ChunkRecord(
    Guid Id,
    string DocumentId,
    int ChunkIndex,
    int ChunkCount,
    string Url,
    string Domain,
    string SourceName,
    TrustTier Tier,
    string Title,
    string Text,
    DateTimeOffset? PublishedAt,
    DateTimeOffset FetchedAt,
    string Origin,
    string ContentHash);

public sealed record VectorPoint(ChunkRecord Chunk, float[] Vector);

public sealed record SearchHit(ChunkRecord Chunk, double Score);

/// <summary>Optional restrictions applied inside the vector search (not after it).</summary>
public sealed record SearchFilter(IReadOnlyList<TrustTier>? Tiers = null, IReadOnlyList<string>? ExcludeDocumentIds = null);

/// <summary>The vector database. Implemented by QdrantVectorStore.</summary>
public interface IVectorStore
{
    /// <summary>Create the collection and its payload indexes if missing; fail if it exists with a different vector size.</summary>
    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);

    /// <summary>The content hash stored with a document's first chunk, or null if the document isn't indexed.</summary>
    Task<string?> GetContentHashAsync(string documentId, CancellationToken ct);

    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    /// <summary>Delete this document's chunks with ChunkIndex ≥ <paramref name="fromIndex"/> (left over from a longer version).</summary>
    Task DeleteChunksFromAsync(string documentId, int fromIndex, CancellationToken ct);

    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int limit, SearchFilter? filter, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);
}

/// <summary>Deterministic point IDs: the same (document, chunk) always maps to the same ID, so re-indexing overwrites.</summary>
public static class ChunkIds
{
    public static Guid For(string documentId, int chunkIndex)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{documentId}#{chunkIndex}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

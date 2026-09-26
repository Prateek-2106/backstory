using System.Security.Cryptography;
using System.Text;
using Backstory.Core.Contracts;
using Backstory.Core.Messaging;
using Backstory.Core.Trust;
using Microsoft.Extensions.Logging;

namespace Backstory.Core.Indexing;

public enum IndexOutcome
{
    /// <summary>Chunked, embedded and stored.</summary>
    Indexed,

    /// <summary>Same content already stored: nothing to do.</summary>
    Unchanged,

    /// <summary>Nothing worth storing after cleaning (e.g. only boilerplate).</summary>
    Empty,
}

public sealed record IndexResult(IndexOutcome Outcome, int ChunkCount, string ContentHash);

/// <summary>
/// One document → searchable chunks. The step 5 design diagram, top to bottom:
/// C6 trust re-check → unchanged? → clean → chunk → embed → upsert → delete leftovers.
/// </summary>
public sealed class DocumentIndexer(
    TrustRegistry trust,
    IEmbeddingProvider embeddings,
    IVectorStore store,
    IndexingOptions options,
    ILogger<DocumentIndexer> logger)
{
    private readonly TextChunker _chunker = new(options.ChunkWords, options.OverlapWords, options.MinChunkWords);

    public async Task<IndexResult> IndexAsync(SourceDocumentFetched doc, CancellationToken ct)
    {
        // C6: defense in depth. Our own ingestion checked this URL, but a bug or a rogue producer could put
        // anything on the topic. Retrying can't make an untrusted URL trusted, so this is a permanent failure.
        var decision = trust.Evaluate(doc.Url);
        if (!decision.IsTrusted)
            throw new PermanentFailureException($"C6: refusing to index {doc.Url}: {decision.Reason}");

        // Ingestion re-publishes everything after a restart; skip documents whose content hasn't changed.
        var hash = ContentHash(doc);
        if (await store.GetContentHashAsync(doc.DocumentId, ct) == hash)
        {
            logger.LogDebug("Unchanged: {DocumentId} ({Url})", doc.DocumentId, doc.Url);
            return new IndexResult(IndexOutcome.Unchanged, 0, hash);
        }

        var cleaned = BoilerplateFilter.Clean(doc.Text, doc.Title);
        var chunks = _chunker.Chunk(cleaned);
        if (chunks.Count == 0)
        {
            await store.DeleteChunksFromAsync(doc.DocumentId, 0, ct); // an older version may exist
            logger.LogWarning("Nothing to index in {Url} after removing boilerplate", doc.Url);
            return new IndexResult(IndexOutcome.Empty, 0, hash);
        }

        // Title goes into every embedding input, so a chunk still "knows" what it is about on its own.
        var vectors = await embeddings.EmbedAsync(
            chunks.Select(c => $"{doc.Title}\n\n{c.Text}").ToList(), EmbeddingPurpose.Document, ct);

        var source = decision.Source!;
        var points = chunks.Select((c, i) => new VectorPoint(
            new ChunkRecord(
                Id: ChunkIds.For(doc.DocumentId, c.Index),
                DocumentId: doc.DocumentId,
                ChunkIndex: c.Index,
                ChunkCount: chunks.Count,
                Url: doc.Url,
                Domain: doc.Domain,
                SourceName: source.Name,
                Tier: source.Tier,
                Title: doc.Title,
                Text: c.Text,
                PublishedAt: doc.PublishedAt,
                FetchedAt: doc.FetchedAt,
                Origin: doc.Origin,
                ContentHash: hash),
            vectors[i])).ToList();

        await store.UpsertAsync(points, ct);

        // The article may have been longer last time: remove chunks beyond the new end.
        await store.DeleteChunksFromAsync(doc.DocumentId, chunks.Count, ct);

        logger.LogInformation("Indexed {DocumentId}: {Chunks} chunk(s) from {Domain} \"{Title}\"",
            doc.DocumentId, chunks.Count, doc.Domain, doc.Title);
        return new IndexResult(IndexOutcome.Indexed, chunks.Count, hash);
    }

    private static string ContentHash(SourceDocumentFetched doc) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Title + "\n" + doc.Text)))[..16];
}

/// <summary>Question → nearest chunks. Step 6 builds query planning and re-ranking on top of this.</summary>
public sealed class VectorSearch(IEmbeddingProvider embeddings, IVectorStore store)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilter? filter, CancellationToken ct)
    {
        var vector = (await embeddings.EmbedAsync([query], EmbeddingPurpose.Query, ct))[0];
        return await store.SearchAsync(vector, limit, filter, ct);
    }
}

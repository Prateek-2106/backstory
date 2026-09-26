using Backstory.Core.Contracts;
using Backstory.Core.Indexing;
using Backstory.Core.Messaging;
using Backstory.Core.Trust;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backstory.Core.Tests.Indexing;

public class DocumentIndexerTests
{
    private static readonly TrustRegistry Trust = new(
    [
        new TrustedSource("un.org", "United Nations", TrustTier.Primary),
        new TrustedSource("bbc.co.uk", "BBC", TrustTier.Wire, AllowFullText: false),
    ]);

    private readonly FakeEmbeddings _embeddings = new();
    private readonly InMemoryVectorStore _store = new();

    private DocumentIndexer CreateIndexer() => new(Trust, _embeddings, _store,
        new IndexingOptions { ChunkWords = 60, OverlapWords = 10, MinChunkWords = 10 },
        NullLogger<DocumentIndexer>.Instance);

    private static SourceDocumentFetched Doc(string text, string url = "https://news.un.org/en/story/1", string title = "Sudan talks") =>
        new(Backstory.Core.Ingestion.UrlIdentity.DocumentId(url), url, new Uri(url).Host, title, text,
            new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UnixEpoch, "rss-page");

    private static string Paragraphs(int count) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"Paragraph {i} " + string.Join(' ', Enumerable.Repeat("word", 25))));

    [Fact]
    public async Task Index_StoresChunksWithTrustMetadata()
    {
        var doc = Doc(Paragraphs(6));

        var result = await CreateIndexer().IndexAsync(doc, default);

        Assert.Equal(IndexOutcome.Indexed, result.Outcome);
        Assert.True(result.ChunkCount > 1);
        Assert.Equal(result.ChunkCount, _store.Points.Count);

        var first = _store.Points.Values.Single(p => p.Chunk.ChunkIndex == 0).Chunk;
        Assert.Equal(TrustTier.Primary, first.Tier);
        Assert.Equal("United Nations", first.SourceName);
        Assert.Equal(ChunkIds.For(doc.DocumentId, 0), first.Id);

        // Every embedding input carries the title; documents use the Document purpose.
        Assert.All(_embeddings.Inputs, i => Assert.StartsWith("Sudan talks\n\n", i, StringComparison.Ordinal));
        Assert.Equal(EmbeddingPurpose.Document, _embeddings.LastPurpose);
    }

    [Fact]
    public async Task C6_UntrustedUrl_IsPermanentFailure_AndNothingIsEmbedded()
    {
        var doc = Doc(Paragraphs(2), url: "https://evil.example/fake-un-story");

        var ex = await Assert.ThrowsAsync<PermanentFailureException>(() => CreateIndexer().IndexAsync(doc, default));

        Assert.Contains("C6", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_embeddings.Inputs);
        Assert.Empty(_store.Points);
    }

    [Fact]
    public async Task SameDocumentTwice_SecondIsUnchanged_AndNotReEmbedded()
    {
        var indexer = CreateIndexer();
        var doc = Doc(Paragraphs(4));

        await indexer.IndexAsync(doc, default);
        var embedCalls = _embeddings.Inputs.Count;
        var second = await indexer.IndexAsync(doc, default);

        Assert.Equal(IndexOutcome.Unchanged, second.Outcome);
        Assert.Equal(embedCalls, _embeddings.Inputs.Count); // no new embeddings
    }

    [Fact]
    public async Task DocumentGetsShorter_LeftoverChunksAreDeleted()
    {
        var indexer = CreateIndexer();
        var longVersion = await indexer.IndexAsync(Doc(Paragraphs(10)), default);

        var shortVersion = await indexer.IndexAsync(Doc(Paragraphs(2)), default); // same URL, new text

        Assert.True(longVersion.ChunkCount > shortVersion.ChunkCount);
        Assert.Equal(shortVersion.ChunkCount, _store.Points.Count);
        Assert.All(_store.Points.Values, p => Assert.Equal(shortVersion.ContentHash, p.Chunk.ContentHash));
    }

    [Fact]
    public async Task OnlyBoilerplate_IsEmpty_AndOldVersionRemoved()
    {
        var indexer = CreateIndexer();
        await indexer.IndexAsync(Doc(Paragraphs(3)), default);

        var result = await indexer.IndexAsync(Doc("♦ Subscribe here to a topic.\n\nDownload the UN News app."), default);

        Assert.Equal(IndexOutcome.Empty, result.Outcome);
        Assert.Empty(_store.Points);
    }

    [Fact]
    public async Task VectorSearch_EmbedsQueryWithQueryPurpose_AndReturnsNearest()
    {
        await CreateIndexer().IndexAsync(Doc(Paragraphs(4)), default);
        var search = new VectorSearch(_embeddings, _store);

        var hits = await search.SearchAsync("Paragraph 3", limit: 2, filter: null, default);

        Assert.Equal(EmbeddingPurpose.Query, _embeddings.LastPurpose);
        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Score >= hits[1].Score);
    }

    // ---------- fakes ----------

    /// <summary>Deterministic "embedding": letter counts, so similar texts get similar vectors. No model needed.</summary>
    internal sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public List<string> Inputs { get; } = [];
        public EmbeddingPurpose LastPurpose { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
        {
            LastPurpose = purpose;
            Inputs.AddRange(texts);
            IReadOnlyList<float[]> vectors = texts.Select(t =>
            {
                var v = new float[26];
                foreach (var ch in t.ToLowerInvariant().Where(char.IsAsciiLetterLower)) v[ch - 'a']++;
                return v;
            }).ToList();
            return Task.FromResult(vectors);
        }
    }

    /// <summary>A dictionary pretending to be Qdrant, with brute-force cosine search.</summary>
    internal sealed class InMemoryVectorStore : IVectorStore
    {
        public Dictionary<Guid, VectorPoint> Points { get; } = [];

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task<string?> GetContentHashAsync(string documentId, CancellationToken ct) =>
            Task.FromResult(Points.TryGetValue(ChunkIds.For(documentId, 0), out var p) ? p.Chunk.ContentHash : null);

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            foreach (var p in points) Points[p.Chunk.Id] = p;
            return Task.CompletedTask;
        }

        public Task DeleteChunksFromAsync(string documentId, int fromIndex, CancellationToken ct)
        {
            foreach (var id in Points.Values.Where(p => p.Chunk.DocumentId == documentId && p.Chunk.ChunkIndex >= fromIndex)
                         .Select(p => p.Chunk.Id).ToList())
                Points.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int limit, SearchFilter? filter, CancellationToken ct)
        {
            IReadOnlyList<SearchHit> hits = Points.Values
                .Where(p => filter?.Tiers is null || filter.Tiers.Contains(p.Chunk.Tier))
                .Select(p => new SearchHit(p.Chunk, Cosine(vector, p.Vector)))
                .OrderByDescending(h => h.Score)
                .Take(limit)
                .ToList();
            return Task.FromResult(hits);
        }

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult((long)Points.Count);

        private static double Cosine(float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
        }
    }
}

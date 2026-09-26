using Backstory.Core.Indexing;
using Backstory.Core.Trust;
using Backstory.Core.VectorStore;

namespace Backstory.Infrastructure.Tests.Qdrant;

/// <summary>
/// The Qdrant REST client against a real Qdrant (docker compose locally, a service container in CI).
/// Each test gets its own throwaway collection with tiny 4-dimensional vectors.
/// Needs Qdrant on localhost:6333, or set QDRANT_URL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QdrantVectorStoreIntegrationTests : IAsyncLifetime
{
    private static readonly string QdrantUrl = Environment.GetEnvironmentVariable("QDRANT_URL") ?? "http://localhost:6333";

    private readonly QdrantVectorStore _store = new(new HttpClient(), QdrantUrl, $"it_{Guid.NewGuid():N}");

    public Task InitializeAsync() => _store.EnsureCollectionAsync(4, CancellationToken.None);

    public Task DisposeAsync() => _store.DeleteCollectionAsync(CancellationToken.None);

    private static VectorPoint Point(string docId, int index, int count, TrustTier tier, float[] vector, string hash = "h1") =>
        new(new ChunkRecord(ChunkIds.For(docId, index), docId, index, count, $"https://news.un.org/{docId}", "news.un.org",
            "United Nations", tier, $"Title {docId}", $"Text {docId} #{index}",
            new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero),
            "rss-page", hash), vector);

    [Fact]
    public async Task Upsert_ThenSearch_ReturnsNearestFirst_WithFullPayload()
    {
        await _store.UpsertAsync(
        [
            Point("doc-a", 0, 1, TrustTier.Primary, [1, 0, 0, 0]),
            Point("doc-b", 0, 1, TrustTier.Wire, [0, 1, 0, 0]),
            Point("doc-c", 0, 1, TrustTier.Reference, [0.9f, 0.1f, 0, 0]),
        ], default);

        var hits = await _store.SearchAsync([1, 0, 0, 0], 2, null, default);

        Assert.Equal(new[] { "doc-a", "doc-c" }, hits.Select(h => h.Chunk.DocumentId));
        Assert.True(hits[0].Score > hits[1].Score);
        var top = hits[0].Chunk;
        Assert.Equal(TrustTier.Primary, top.Tier);
        Assert.Equal("United Nations", top.SourceName);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), top.PublishedAt);
        Assert.Equal(3, await _store.CountAsync(default));
    }

    [Fact]
    public async Task Search_TierFilter_IsAppliedInsideQdrant()
    {
        await _store.UpsertAsync(
        [
            Point("doc-a", 0, 1, TrustTier.Reference, [1, 0, 0, 0]),   // closest, but tier 3
            Point("doc-b", 0, 1, TrustTier.Primary, [0.5f, 0.5f, 0, 0]),
        ], default);

        var hits = await _store.SearchAsync([1, 0, 0, 0], 5, new SearchFilter(Tiers: [TrustTier.Primary]), default);

        Assert.Equal("doc-b", Assert.Single(hits).Chunk.DocumentId);
    }

    [Fact]
    public async Task UpsertSameIds_Overwrites_NotDuplicates()
    {
        await _store.UpsertAsync([Point("doc-a", 0, 1, TrustTier.Primary, [1, 0, 0, 0], hash: "old")], default);
        await _store.UpsertAsync([Point("doc-a", 0, 1, TrustTier.Primary, [1, 0, 0, 0], hash: "new")], default);

        Assert.Equal(1, await _store.CountAsync(default));
        Assert.Equal("new", await _store.GetContentHashAsync("doc-a", default));
        Assert.Null(await _store.GetContentHashAsync("doc-missing", default));
    }

    [Fact]
    public async Task DeleteChunksFrom_RemovesOnlyThatDocumentsTail()
    {
        await _store.UpsertAsync(
        [
            Point("doc-a", 0, 3, TrustTier.Primary, [1, 0, 0, 0]),
            Point("doc-a", 1, 3, TrustTier.Primary, [0, 1, 0, 0]),
            Point("doc-a", 2, 3, TrustTier.Primary, [0, 0, 1, 0]),
            Point("doc-b", 1, 2, TrustTier.Primary, [0, 0, 0, 1]),
        ], default);

        await _store.DeleteChunksFromAsync("doc-a", 1, default);

        var remaining = await _store.SearchAsync([1, 1, 1, 1], 10, null, default);
        Assert.Equal(new[] { "doc-a#0", "doc-b#1" },
            remaining.Select(h => $"{h.Chunk.DocumentId}#{h.Chunk.ChunkIndex}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EnsureCollection_IsIdempotent_ButRejectsDifferentVectorSize()
    {
        await _store.EnsureCollectionAsync(4, default); // second call: no error

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.EnsureCollectionAsync(768, default));
    }
}

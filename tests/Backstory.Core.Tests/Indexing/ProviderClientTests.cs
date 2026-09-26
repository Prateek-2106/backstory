using System.Net;
using System.Text.Json.Nodes;
using Backstory.Core.Indexing;
using Backstory.Core.Providers;
using Backstory.Core.Tests.Ingestion;
using Backstory.Core.Trust;
using Backstory.Core.VectorStore;

namespace Backstory.Core.Tests.Indexing;

/// <summary>Checks the exact HTTP requests we send to Ollama and Qdrant, and how we read their answers.</summary>
public class OllamaEmbeddingProviderTests
{
    private readonly FakeHttp _web = new();

    private static string EmbedResponse(int count, int dims) =>
        new JsonObject
        {
            ["embeddings"] = new JsonArray(Enumerable.Range(0, count)
                .Select(i => (JsonNode)new JsonArray(Enumerable.Range(0, dims).Select(d => (JsonNode)JsonValue.Create((float)(i + d)))
                    .ToArray())).ToArray()),
        }.ToJsonString();

    [Fact]
    public async Task Embed_AddsTaskPrefix_BatchesRequests_AndReturnsVectorsInOrder()
    {
        _web.Json("http://ollama:11434/api/embed", EmbedResponse(count: 2, dims: 3));
        var provider = new OllamaEmbeddingProvider(new HttpClient(_web),
            new IndexingOptions { OllamaUrl = "http://ollama:11434", EmbeddingDimensions = 3, EmbeddingBatchSize = 2 });

        var vectors = await provider.EmbedAsync(["a", "b", "c", "d"], EmbeddingPurpose.Document, default);

        Assert.Equal(4, vectors.Count);
        Assert.Equal(2, _web.Requests.Count); // 4 texts / batch of 2
        var first = JsonNode.Parse(_web.Requests[0].Body!)!;
        Assert.Equal("nomic-embed-text", first["model"]!.GetValue<string>());
        Assert.Equal("search_document: a", first["input"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Embed_QueryPurpose_UsesQueryPrefix()
    {
        _web.Json("http://ollama:11434/api/embed", EmbedResponse(count: 1, dims: 3));
        var provider = new OllamaEmbeddingProvider(new HttpClient(_web),
            new IndexingOptions { OllamaUrl = "http://ollama:11434", EmbeddingDimensions = 3 });

        await provider.EmbedAsync(["sudan"], EmbeddingPurpose.Query, default);

        Assert.Equal("search_query: sudan", JsonNode.Parse(_web.Requests[0].Body!)!["input"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Embed_WrongDimensions_FailsLoudly()
    {
        _web.Json("http://ollama:11434/api/embed", EmbedResponse(count: 1, dims: 384));
        var provider = new OllamaEmbeddingProvider(new HttpClient(_web),
            new IndexingOptions { OllamaUrl = "http://ollama:11434", EmbeddingDimensions = 768 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync(["x"], EmbeddingPurpose.Query, default));

        Assert.Contains("384 dimensions", ex.Message, StringComparison.Ordinal);
    }
}

public class QdrantVectorStoreTests
{
    private const string Base = "http://qdrant:6333";
    private readonly FakeHttp _web = new();

    private QdrantVectorStore Store() => new(new HttpClient(_web), Base, "chunks");

    [Fact]
    public async Task EnsureCollection_Missing_CreatesItWithCosineAndPayloadIndexes()
    {
        _web.Json($"{Base}/collections/chunks", "{}"); // will be replaced below for GET
        var created = false;
        _web.Add($"{Base}/collections/chunks", () =>
        {
            if (!created) { created = true; return new HttpResponseMessage(HttpStatusCode.NotFound); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        _web.Json($"{Base}/collections/chunks/index?wait=true", "{}");

        await Store().EnsureCollectionAsync(768, default);

        var create = _web.Requests.Single(r => r.Method == HttpMethod.Put && r.Url.EndsWith("/collections/chunks", StringComparison.Ordinal));
        var body = JsonNode.Parse(create.Body!)!;
        Assert.Equal(768, body["vectors"]!["size"]!.GetValue<int>());
        Assert.Equal("Cosine", body["vectors"]!["distance"]!.GetValue<string>());
        Assert.Equal(5, _web.Requests.Count(r => r.Url.Contains("/index", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task EnsureCollection_ExistsWithDifferentSize_Throws()
    {
        _web.Json($"{Base}/collections/chunks", """{"result":{"config":{"params":{"vectors":{"size":384,"distance":"Cosine"}}}}}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Store().EnsureCollectionAsync(768, default));

        Assert.Contains("384", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_SendsTrustFilter_AndMapsPayloadBack()
    {
        var id = Guid.NewGuid();
        _web.Json($"{Base}/collections/chunks/points/search", $$$"""
            {"result":[{"id":"{{{id}}}","score":0.87,"payload":{
              "documentId":"doc-1","chunkIndex":2,"chunkCount":5,"url":"https://news.un.org/1","domain":"news.un.org",
              "sourceName":"United Nations","trustTier":1,"title":"Sudan","text":"Council met.",
              "publishedAt":"2026-09-20T00:00:00+00:00","fetchedAt":"2026-09-26T00:00:00+00:00","origin":"rss-page","contentHash":"abc"}}]}
            """);

        var hits = await Store().SearchAsync([0.1f, 0.2f], 5,
            new SearchFilter(Tiers: [TrustTier.Primary, TrustTier.Wire], ExcludeDocumentIds: ["doc-9"]), default);

        var hit = Assert.Single(hits);
        Assert.Equal(0.87, hit.Score, 3);
        Assert.Equal(id, hit.Chunk.Id);
        Assert.Equal(TrustTier.Primary, hit.Chunk.Tier);
        Assert.Equal(2, hit.Chunk.ChunkIndex);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), hit.Chunk.PublishedAt);

        var sent = JsonNode.Parse(_web.Requests.Single().Body!)!;
        Assert.Equal(5, sent["limit"]!.GetValue<int>());
        Assert.Equal("trustTier", sent["filter"]!["must"]![0]!["key"]!.GetValue<string>());
        Assert.Equal(2, sent["filter"]!["must"]![0]!["match"]!["any"]!.AsArray().Count);
        Assert.Equal("doc-9", sent["filter"]!["must_not"]![0]!["match"]!["any"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task GetContentHash_MissingPoint_IsNull()
    {
        Assert.Null(await Store().GetContentHashAsync("doc-unknown", default)); // FakeHttp answers 404
    }

    [Fact]
    public async Task DeleteChunksFrom_SendsDocumentAndRangeFilter()
    {
        _web.Json($"{Base}/collections/chunks/points/delete?wait=true", "{}");

        await Store().DeleteChunksFromAsync("doc-1", 3, default);

        var body = JsonNode.Parse(_web.Requests.Single().Body!)!;
        Assert.Equal("doc-1", body["filter"]!["must"]![0]!["match"]!["value"]!.GetValue<string>());
        Assert.Equal(3, body["filter"]!["must"]![1]!["range"]!["gte"]!.GetValue<int>());
    }
}

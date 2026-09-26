using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backstory.Core.Indexing;
using Backstory.Core.Trust;

namespace Backstory.Core.VectorStore;

/// <summary>
/// Qdrant over its REST API (no client library needed). Endpoints used:
///   GET  /collections/{c}                    exists? which vector size?
///   PUT  /collections/{c}                    create (cosine distance)
///   PUT  /collections/{c}/index              payload indexes, so filters stay fast as data grows
///   PUT  /collections/{c}/points?wait=true   upsert
///   GET  /collections/{c}/points/{id}        read one point (content hash check)
///   POST /collections/{c}/points/delete      delete by filter
///   POST /collections/{c}/points/search      nearest neighbours, with payload
///   POST /collections/{c}/points/count
/// </summary>
public sealed class QdrantVectorStore(HttpClient http, string baseUrl, string collection) : IVectorStore
{
    private readonly Uri _base = new(baseUrl.TrimEnd('/') + "/");

    private Uri Url(string path) => new(_base, $"collections/{collection}{path}");

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        using var existing = await http.GetAsync(Url(""), ct);
        if (existing.IsSuccessStatusCode)
        {
            var info = await existing.Content.ReadFromJsonAsync<JsonObject>(ct);
            var size = info?["result"]?["config"]?["params"]?["vectors"]?["size"]?.GetValue<int>();
            if (size != dimensions)
                throw new InvalidOperationException(
                    $"Qdrant collection '{collection}' has vector size {size}, but the embedding model produces {dimensions}. " +
                    "Changing embedding model means re-indexing into a new collection.");
            return;
        }
        if (existing.StatusCode != HttpStatusCode.NotFound)
            await ThrowAsync(existing, "read collection", ct);

        await SendAsync(HttpMethod.Put, Url(""), new JsonObject
        {
            ["vectors"] = new JsonObject { ["size"] = dimensions, ["distance"] = "Cosine" },
        }, "create collection", ct);

        foreach (var (field, schema) in new[]
                 {
                     ("documentId", "keyword"), ("domain", "keyword"), ("trustTier", "integer"),
                     ("chunkIndex", "integer"), ("publishedAt", "datetime"),
                 })
        {
            await SendAsync(HttpMethod.Put, Url("/index?wait=true"),
                new JsonObject { ["field_name"] = field, ["field_schema"] = schema }, $"index {field}", ct);
        }
    }

    public async Task<string?> GetContentHashAsync(string documentId, CancellationToken ct)
    {
        using var response = await http.GetAsync(Url($"/points/{ChunkIds.For(documentId, 0)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, "read point", ct);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["result"]?["payload"]?["contentHash"]?.GetValue<string>();
    }

    public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return Task.CompletedTask;

        var array = new JsonArray();
        foreach (var p in points)
        {
            array.Add(new JsonObject
            {
                ["id"] = p.Chunk.Id.ToString(),
                ["vector"] = new JsonArray(p.Vector.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
                ["payload"] = ToPayload(p.Chunk),
            });
        }

        return SendAsync(HttpMethod.Put, Url("/points?wait=true"), new JsonObject { ["points"] = array }, "upsert", ct);
    }

    public Task DeleteChunksFromAsync(string documentId, int fromIndex, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Url("/points/delete?wait=true"), new JsonObject
        {
            ["filter"] = new JsonObject
            {
                ["must"] = new JsonArray(
                    new JsonObject { ["key"] = "documentId", ["match"] = new JsonObject { ["value"] = documentId } },
                    new JsonObject { ["key"] = "chunkIndex", ["range"] = new JsonObject { ["gte"] = fromIndex } }),
            },
        }, "delete stale chunks", ct);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int limit, SearchFilter? filter, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["vector"] = new JsonArray(vector.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
            ["limit"] = limit,
            ["with_payload"] = true,
        };
        if (ToFilter(filter) is { } f) body["filter"] = f;

        using var response = await http.PostAsJsonAsync(Url("/points/search"), body, ct);
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, "search", ct);

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var hits = new List<SearchHit>();
        foreach (var hit in json?["result"]?.AsArray() ?? [])
        {
            var id = Guid.Parse(hit!["id"]!.GetValue<string>());
            hits.Add(new SearchHit(FromPayload(id, hit["payload"]!.AsObject()), hit["score"]!.GetValue<double>()));
        }
        return hits;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(Url("/points/count"), new JsonObject { ["exact"] = true }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return 0;
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, "count", ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["result"]?["count"]?.GetValue<long>() ?? 0;
    }

    /// <summary>For tests and admin tooling.</summary>
    public async Task DeleteCollectionAsync(CancellationToken ct)
    {
        using var response = await http.DeleteAsync(Url(""), ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            await ThrowAsync(response, "delete collection", ct);
    }

    // ---------- payload mapping ----------

    private static JsonObject ToPayload(ChunkRecord c) => new()
    {
        ["documentId"] = c.DocumentId,
        ["chunkIndex"] = c.ChunkIndex,
        ["chunkCount"] = c.ChunkCount,
        ["url"] = c.Url,
        ["domain"] = c.Domain,
        ["sourceName"] = c.SourceName,
        ["trustTier"] = (int)c.Tier,
        ["title"] = c.Title,
        ["text"] = c.Text,
        ["publishedAt"] = c.PublishedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["fetchedAt"] = c.FetchedAt.ToString("O", CultureInfo.InvariantCulture),
        ["origin"] = c.Origin,
        ["contentHash"] = c.ContentHash,
    };

    private static ChunkRecord FromPayload(Guid id, JsonObject p) => new(
        id,
        p["documentId"]!.GetValue<string>(),
        p["chunkIndex"]!.GetValue<int>(),
        p["chunkCount"]?.GetValue<int>() ?? 0,
        p["url"]!.GetValue<string>(),
        p["domain"]!.GetValue<string>(),
        p["sourceName"]?.GetValue<string>() ?? "",
        (TrustTier)p["trustTier"]!.GetValue<int>(),
        p["title"]?.GetValue<string>() ?? "",
        p["text"]!.GetValue<string>(),
        p["publishedAt"]?.GetValue<string>() is { } published ? DateTimeOffset.Parse(published, CultureInfo.InvariantCulture) : null,
        p["fetchedAt"]?.GetValue<string>() is { } fetched ? DateTimeOffset.Parse(fetched, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue,
        p["origin"]?.GetValue<string>() ?? "",
        p["contentHash"]?.GetValue<string>() ?? "");

    private static JsonObject? ToFilter(SearchFilter? filter)
    {
        if (filter is null) return null;
        var must = new JsonArray();
        var mustNot = new JsonArray();

        if (filter.Tiers is { Count: > 0 } tiers)
            must.Add(new JsonObject
            {
                ["key"] = "trustTier",
                ["match"] = new JsonObject { ["any"] = new JsonArray(tiers.Select(t => (JsonNode)JsonValue.Create((int)t)).ToArray()) },
            });

        if (filter.ExcludeDocumentIds is { Count: > 0 } excluded)
            mustNot.Add(new JsonObject
            {
                ["key"] = "documentId",
                ["match"] = new JsonObject { ["any"] = new JsonArray(excluded.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()) },
            });

        if (must.Count == 0 && mustNot.Count == 0) return null;
        var result = new JsonObject();
        if (must.Count > 0) result["must"] = must;
        if (mustNot.Count > 0) result["must_not"] = mustNot;
        return result;
    }

    // ---------- HTTP helpers ----------

    private async Task SendAsync(HttpMethod method, Uri url, JsonObject body, string what, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) await ThrowAsync(response, what, ct);
    }

    private static async Task ThrowAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Qdrant {what} failed: HTTP {(int)response.StatusCode} {body}");
    }
}

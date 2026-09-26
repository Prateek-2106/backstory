using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backstory.Core.Indexing;

namespace Backstory.Core.Providers;

/// <summary>
/// Embeddings from a local Ollama server: POST /api/embed {"model", "input": [...]} → {"embeddings": [[...], ...]}.
/// Sends texts in batches, adds the model's task prefix, and checks every vector has the expected size.
/// </summary>
public sealed class OllamaEmbeddingProvider(HttpClient http, IndexingOptions options) : IEmbeddingProvider
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct)
    {
        var prefix = purpose == EmbeddingPurpose.Query ? options.QueryPrefix : options.DocumentPrefix;
        var result = new List<float[]>(texts.Count);

        foreach (var batch in texts.Chunk(Math.Max(1, options.EmbeddingBatchSize)))
        {
            var body = new JsonObject
            {
                ["model"] = options.EmbeddingModel,
                ["input"] = new JsonArray(batch.Select(t => (JsonNode)JsonValue.Create(prefix + t)!).ToArray()),
            };

            using var response = await http.PostAsJsonAsync(new Uri(new Uri(options.OllamaUrl), "/api/embed"), body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Ollama /api/embed returned {(int)response.StatusCode}: {error}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                       ?? throw new InvalidOperationException("Ollama returned an empty body.");
            var embeddings = json["embeddings"]?.AsArray()
                             ?? throw new InvalidOperationException("Ollama response has no 'embeddings'.");
            if (embeddings.Count != batch.Length)
                throw new InvalidOperationException($"Asked Ollama for {batch.Length} embeddings, got {embeddings.Count}.");

            foreach (var embedding in embeddings)
            {
                var vector = embedding!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
                if (vector.Length != options.EmbeddingDimensions)
                    throw new InvalidOperationException(
                        $"Model '{options.EmbeddingModel}' returned {vector.Length} dimensions; config says {options.EmbeddingDimensions}.");
                result.Add(vector);
            }
        }

        return result;
    }
}

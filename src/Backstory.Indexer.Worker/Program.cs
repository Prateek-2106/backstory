// Indexer Worker: sources.documents.v1 → trust re-check (C6) → chunk → embed → Qdrant → sources.indexed.v1
//
//   dotnet run --project src/Backstory.Indexer.Worker
//   http://localhost:5102/search?q=humanitarian%20access%20in%20Sudan   semantic search over what's indexed
//   http://localhost:5102/indexer/status                                 how many chunks are stored
//   http://localhost:5102/healthz/live

using Backstory.Core.Contracts;
using Backstory.Core.Indexing;
using Backstory.Core.Providers;
using Backstory.Core.Trust;
using Backstory.Core.VectorStore;
using Backstory.Indexer.Worker;
using Backstory.Infrastructure.Kafka;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddKafka(builder.Configuration);
builder.Services.Configure<IndexingOptions>(builder.Configuration.GetSection(IndexingOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IndexingOptions>>().Value);

builder.Services.AddSingleton(sp => TrustRegistry.FromFile(
    Path.Combine(AppContext.BaseDirectory, sp.GetRequiredService<IndexingOptions>().TrustedSourcesPath)));

builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
{
    var options = sp.GetRequiredService<IndexingOptions>();
    return new OllamaEmbeddingProvider(new HttpClient { Timeout = options.EmbeddingTimeout }, options);
});

builder.Services.AddSingleton<IVectorStore>(sp =>
{
    var options = sp.GetRequiredService<IndexingOptions>();
    return new QdrantVectorStore(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options.QdrantUrl, options.Collection);
});

builder.Services.AddSingleton<DocumentIndexer>();
builder.Services.AddSingleton<VectorSearch>();

// Order matters: hosted services start in registration order, and StartupChecks must pass
// (Qdrant collection exists, Ollama returns the right vector size) before the consumer starts pulling messages.
builder.Services.AddHostedService<StartupChecks>();
builder.Services.AddKafkaConsumer<SourceDocumentFetched, IndexDocumentHandler>(Topics.SourceDocuments, groupId: "indexer");

var app = builder.Build();

app.MapGet("/healthz/live", () => Results.Ok("ok"));

app.MapGet("/indexer/status", async (IVectorStore store, IndexingOptions options, CancellationToken ct) =>
    Results.Ok(new { collection = options.Collection, chunks = await store.CountAsync(ct) }));

// Try it: /search?q=humanitarian access in Sudan&k=5   (add &tier=1 for primary sources only)
app.MapGet("/search", async (string q, int? k, int? tier, VectorSearch search, CancellationToken ct) =>
{
    var filter = tier is { } t ? new SearchFilter(Tiers: [(TrustTier)t]) : null;
    var hits = await search.SearchAsync(q, Math.Clamp(k ?? 5, 1, 50), filter, ct);
    return Results.Ok(hits.Select(h => new
    {
        score = Math.Round(h.Score, 4),
        h.Chunk.Title,
        h.Chunk.Url,
        h.Chunk.SourceName,
        trustTier = (int)h.Chunk.Tier,
        chunk = $"{h.Chunk.ChunkIndex + 1}/{h.Chunk.ChunkCount}",
        h.Chunk.PublishedAt,
        h.Chunk.Text,
    }));
});

app.Run();

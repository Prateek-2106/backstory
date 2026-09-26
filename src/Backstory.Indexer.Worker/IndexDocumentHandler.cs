using Backstory.Core.Contracts;
using Backstory.Core.Indexing;
using Backstory.Core.Messaging;

namespace Backstory.Indexer.Worker;

/// <summary>
/// Kafka → DocumentIndexer → SourceIndexed. All the logic lives in DocumentIndexer (Core);
/// this class only connects it to the bus.
/// Exceptions do the signalling: PermanentFailureException (untrusted URL) → dead-letter now;
/// anything else (Ollama or Qdrant down) → retried, then dead-lettered.
/// </summary>
public sealed class IndexDocumentHandler(DocumentIndexer indexer, IEventPublisher publisher) : IEventHandler<SourceDocumentFetched>
{
    public async Task HandleAsync(EventEnvelope<SourceDocumentFetched> envelope, MessageContext context, CancellationToken ct)
    {
        var doc = envelope.Data;
        var result = await indexer.IndexAsync(doc, ct);

        if (result.Outcome != IndexOutcome.Indexed)
            return; // unchanged or empty: nothing new to announce

        var indexed = new SourceIndexed(doc.DocumentId, doc.Domain, result.ChunkCount, DateTimeOffset.UtcNow);
        await publisher.PublishAsync(Topics.SourcesIndexed, doc.DocumentId,
            EventEnvelope<SourceIndexed>.Create("indexer-worker", indexed, envelope.CorrelationId), ct);
    }
}

/// <summary>
/// Fail fast at startup with a clear message, instead of dead-lettering every document later:
/// the Qdrant collection must exist (created if missing) and Ollama must return vectors of the configured size.
/// Retries for ~30 s because containers may still be starting.
/// </summary>
public sealed class StartupChecks(IVectorStore store, IEmbeddingProvider embeddings, IndexingOptions options, ILogger<StartupChecks> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.EnsureCollectionAsync(options.EmbeddingDimensions, cancellationToken);
                logger.LogInformation("Qdrant collection '{Collection}' ready ({Count} chunks stored)",
                    options.Collection, await store.CountAsync(cancellationToken));

                var probe = await embeddings.EmbedAsync(["startup check"], EmbeddingPurpose.Query, cancellationToken);
                logger.LogInformation("Ollama model '{Model}' ready ({Dims} dimensions)", options.EmbeddingModel, probe[0].Length);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < 6 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Startup check {Attempt}/6 failed ({Error}); retrying in 5 s. Are Qdrant (docker compose) and Ollama running?",
                    attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

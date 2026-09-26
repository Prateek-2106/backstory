// Ingestion Worker: polls feeds, trust-checks every link, publishes documents and articles to Kafka.
//
//   dotnet run --project src/Backstory.Ingestion.Worker
//   http://localhost:5101/ingestion/status   what each feed's last poll did (and why items were skipped)
//   http://localhost:5101/healthz/live       liveness probe for Kubernetes

using System.Net;
using Backstory.Core.Ingestion;
using Backstory.Core.Trust;
using Backstory.Infrastructure.Kafka;
using Backstory.Ingestion.Worker;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddKafka(builder.Configuration);
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IngestionOptions>>().Value);

// The allowlist. Loading fails fast at startup if the file is missing or invalid.
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IngestionOptions>();
    return TrustRegistry.FromFile(Path.Combine(AppContext.BaseDirectory, options.TrustedSourcesPath));
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DomainRateLimiter>();
builder.Services.AddSingleton<SeenUrlCache>();
builder.Services.AddSingleton<FeedStatusBoard>();

// One long-lived HttpClient. AllowAutoRedirect = false is what makes checkpoint C3 possible:
// every redirect comes back to SafeFetcher to be trust-checked instead of being followed silently.
builder.Services.AddSingleton(sp => new SafeFetcher(
    new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), // picks up DNS changes
    })
    { Timeout = sp.GetRequiredService<IngestionOptions>().RequestTimeout },
    sp.GetRequiredService<TrustRegistry>(),
    sp.GetRequiredService<DomainRateLimiter>(),
    sp.GetRequiredService<IngestionOptions>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<SafeFetcher>>()));

builder.Services.AddSingleton<IngestionPipeline>();
builder.Services.AddHostedService<FeedPollingService>();

var app = builder.Build();

app.MapGet("/healthz/live", () => Results.Ok("ok"));
app.MapGet("/ingestion/status", (FeedStatusBoard board) => Results.Ok(board.Snapshot()));

app.Run();

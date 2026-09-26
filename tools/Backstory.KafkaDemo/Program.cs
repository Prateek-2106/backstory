// Step 3 demo. Run from the repo root:
//
//   dotnet run --project tools/Backstory.KafkaDemo                publish 3 articles, consume them
//   dotnet run --project tools/Backstory.KafkaDemo -- --fail       handler always throws -> retries -> DLQ
//   dotnet run --project tools/Backstory.KafkaDemo -- --poison     also send one unparseable message -> DLQ at once
//   dotnet run --project tools/Backstory.KafkaDemo -- --count 10
//
// What happens:
//   1. The host starts a KafkaConsumerWorker on news.articles.v1 (group "kafka-demo").
//   2. We publish N ArticlePublished events through IEventPublisher.
//   3. The worker reads them and calls DemoArticleHandler (set a breakpoint in it).
//   4. When every message is finished, the demo stops.

using System.Text;
using Backstory.Core.Contracts;
using Backstory.Core.Messaging;
using Backstory.Infrastructure.Kafka;
using Backstory.KafkaDemo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var fail = args.Contains("--fail");
var poison = args.Contains("--poison");
var count = args.SkipWhile(a => a != "--count").Skip(1).Select(int.Parse).FirstOrDefault(3);

// Our flags are not configuration, so args are not passed in. ContentRootPath makes appsettings.json
// load from the build output, whichever folder you run from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddKafka(builder.Configuration);
builder.Services.AddSingleton(new DemoState(fail));
builder.Services.AddKafkaConsumer<ArticlePublished, DemoArticleHandler>(Topics.Articles, groupId: "kafka-demo");

using var host = builder.Build();
await host.StartAsync();

var state = host.Services.GetRequiredService<DemoState>();
var maxAttempts = host.Services.GetRequiredService<IOptions<KafkaOptions>>().Value.Retry.MaxAttempts;
state.ExpectCalls(fail ? count * maxAttempts : count);

Console.WriteLine($"\n== Publishing {count} article(s) to {Topics.Articles} (fail mode: {fail})");
var publisher = host.Services.GetRequiredService<IEventPublisher>();
for (var i = 1; i <= count; i++)
{
    var article = new ArticlePublished(
        ArticleId: $"demo-{Guid.NewGuid():N}"[..13],
        Headline: $"Demo headline #{i}",
        Summary: "Published by the step 3 Kafka demo.",
        Body: null,
        Url: null,
        Section: "demo",
        PublishedAt: DateTimeOffset.UtcNow);

    var receipt = await publisher.PublishAsync(Topics.Articles, article.ArticleId, EventEnvelope<ArticlePublished>.Create("kafka-demo", article));
    Console.WriteLine($"   published {article.ArticleId} -> partition {receipt.Partition}, offset {receipt.Offset}");
}

if (poison)
{
    var raw = host.Services.GetRequiredService<KafkaEventPublisher>();
    var receipt = await raw.PublishRawAsync(Topics.Articles, "poison", Encoding.UTF8.GetBytes("this is not JSON"));
    Console.WriteLine($"   published a POISON message -> partition {receipt.Partition}, offset {receipt.Offset}");
}

Console.WriteLine("\n== Waiting for the consumer (first run: ~5 s while the group forms)...\n");
var finished = await Task.WhenAny(state.Done, Task.Delay(TimeSpan.FromSeconds(45))) == state.Done;

// Let the last dead letter / offset commit go out before stopping.
await Task.Delay(TimeSpan.FromSeconds(poison || fail ? 3 : 1));
await host.StopAsync();

Console.WriteLine(finished
    ? $"\n== Done: handler was called {state.Calls} time(s)."
    : $"\n== Timed out: handler was called {state.Calls} time(s), expected {state.ExpectedCalls}. Is Kafka running?");

if (fail || poison)
{
    Console.WriteLine($"== Dead letters are in {Topics.DeadLetter(Topics.Articles)}. Look in Kafka UI (http://localhost:8081) -> Topics -> {Topics.DeadLetter(Topics.Articles)} -> Messages -> Headers.");
}

return finished ? 0 : 1;

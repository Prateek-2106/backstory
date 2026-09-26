using Backstory.Core.Messaging;
using Backstory.Infrastructure.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backstory.Infrastructure.Tests.Kafka;

/// <summary>No broker needed: creating and disposing a producer does not connect anywhere.</summary>
public class KafkaEventPublisherLifetimeTests
{
    [Fact]
    public void ContainerDisposal_DisposesSharedPublisherOncePerRegistration_WithoutThrowing()
    {
        // Same setup as every service: one publisher registered under three types.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kafka:BootstrapServers"] = "localhost:9092" })
            .Build();
        var services = new ServiceCollection().AddLogging();
        services.AddKafka(configuration);
        var provider = services.BuildServiceProvider();

        var concrete = provider.GetRequiredService<KafkaEventPublisher>();
        Assert.Same(concrete, provider.GetRequiredService<IEventPublisher>());
        Assert.Same(concrete, provider.GetRequiredService<IDeadLetterSink>());

        // Regression: this used to throw ObjectDisposedException ("handle is destroyed")
        // because the container calls Dispose() on the same object three times.
        provider.Dispose();
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        var publisher = new KafkaEventPublisher(
            Microsoft.Extensions.Options.Options.Create(new KafkaOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KafkaEventPublisher>.Instance);

        publisher.Dispose();
        publisher.Dispose();
    }
}

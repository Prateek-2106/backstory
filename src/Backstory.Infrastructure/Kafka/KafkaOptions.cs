using Backstory.Core.Messaging;

namespace Backstory.Infrastructure.Kafka;

/// <summary>Bound from the "Kafka" section of appsettings.json / environment variables (Kafka__BootstrapServers).</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>localhost:9092 from your machine; kafka:19092 from inside Docker; the Strimzi service in Kubernetes.</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Shows up in broker logs and Kafka UI, so you can tell which service is connected.</summary>
    public string ClientId { get; set; } = "backstory";

    public RetryOptions Retry { get; set; } = new();
}

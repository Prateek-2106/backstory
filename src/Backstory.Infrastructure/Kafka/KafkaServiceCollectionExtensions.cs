using Backstory.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backstory.Infrastructure.Kafka;

/// <summary>
/// One-line setup for services:
///   builder.Services.AddKafka(builder.Configuration);
///   builder.Services.AddKafkaConsumer&lt;ArticlePublished, MyHandler&gt;(Topics.Articles, "context-worker");
/// </summary>
public static class KafkaServiceCollectionExtensions
{
    public static IServiceCollection AddKafka(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));

        // One producer shared by everything in the process, exposed under both interfaces.
        services.AddSingleton<KafkaEventPublisher>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<KafkaEventPublisher>());
        services.AddSingleton<IDeadLetterSink>(sp => sp.GetRequiredService<KafkaEventPublisher>());
        return services;
    }

    /// <summary>Consume <paramref name="topic"/> as group <paramref name="groupId"/>, sending each event to THandler.</summary>
    public static IServiceCollection AddKafkaConsumer<T, THandler>(this IServiceCollection services, string topic, string groupId)
        where THandler : class, IEventHandler<T>
    {
        services.AddSingleton<THandler>();

        // Registered as IHostedService directly (not AddHostedService) so two consumers of the same event type are both kept.
        services.AddSingleton<IHostedService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger($"Kafka.{groupId}.{topic}");

            var processor = new MessageProcessor<T>(
                sp.GetRequiredService<THandler>(),
                sp.GetRequiredService<IDeadLetterSink>(),
                options.Retry,
                logger);

            return new KafkaConsumerWorker<T>(topic, groupId, processor, options, logger);
        });

        return services;
    }
}

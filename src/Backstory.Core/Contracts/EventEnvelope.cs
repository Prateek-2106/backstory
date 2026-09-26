namespace Backstory.Core.Contracts;

/// <summary>
/// Every Kafka message is one of these (modelled on CloudEvents).
/// The envelope carries the metadata every consumer needs, whatever the payload:
/// </summary>
/// <param name="Id">Unique per message. Consumers use it to spot duplicates.</param>
/// <param name="Type">"backstory.articlepublished" etc. Lets a consumer reject a message it does not understand.</param>
/// <param name="Source">Which service produced it, e.g. "gateway-api". Useful when debugging.</param>
/// <param name="Time">When it was produced (UTC).</param>
/// <param name="SchemaVersion">Bumped on additive payload changes; breaking changes get a new topic.</param>
/// <param name="CorrelationId">Same value on every event caused by one original action, so you can follow one article through all services.</param>
/// <param name="Data">The payload.</param>
public sealed record EventEnvelope<T>(
    string Id,
    string Type,
    string Source,
    DateTimeOffset Time,
    int SchemaVersion,
    string CorrelationId,
    T Data)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Wrap a payload. Pass the incoming event's CorrelationId when this event is caused by another one.</summary>
    public static EventEnvelope<T> Create(string source, T data, string? correlationId = null) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Type: EventTypes.For<T>(),
            Source: source,
            Time: DateTimeOffset.UtcNow,
            SchemaVersion: CurrentSchemaVersion,
            CorrelationId: correlationId ?? Guid.NewGuid().ToString("N"),
            Data: data);
}

public static class EventTypes
{
    /// <summary>ArticlePublished -> "backstory.articlepublished".</summary>
    public static string For<T>() => "backstory." + typeof(T).Name.ToLowerInvariant();
}

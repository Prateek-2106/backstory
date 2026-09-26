using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backstory.Core.Contracts;

/// <summary>
/// Turns envelopes into bytes for Kafka and back. One place, one set of JSON rules,
/// so every service writes and reads exactly the same format.
/// </summary>
public static class EventSerializer
{
    /// <summary>camelCase names, enums as strings, nulls omitted.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] Serialize<T>(EventEnvelope<T> envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    /// <summary>
    /// Parse bytes into an envelope and check it is the event type we expect.
    /// Throws <see cref="InvalidEventException"/> for anything malformed: the Kafka consumer
    /// treats that as a poison message and sends it to the dead-letter topic instead of retrying.
    /// </summary>
    public static EventEnvelope<T> Deserialize<T>(ReadOnlySpan<byte> bytes)
    {
        EventEnvelope<T>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EventEnvelope<T>>(bytes, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidEventException($"Not valid JSON for {typeof(T).Name}: {ex.Message}", ex);
        }

        if (envelope is null)
            throw new InvalidEventException("Message body was JSON null.");

        var expected = EventTypes.For<T>();
        if (envelope.Type != expected)
            throw new InvalidEventException($"Expected type '{expected}' but got '{envelope.Type}'.");

        if (envelope.Data is null)
            throw new InvalidEventException($"Envelope {envelope.Id} has no data.");

        if (envelope.SchemaVersion > EventEnvelope<T>.CurrentSchemaVersion)
            throw new InvalidEventException(
                $"Schema version {envelope.SchemaVersion} is newer than this service understands ({EventEnvelope<T>.CurrentSchemaVersion}).");

        return envelope;
    }
}

/// <summary>A message that can never be processed, no matter how often it is retried.</summary>
public sealed class InvalidEventException : Exception
{
    public InvalidEventException() { }
    public InvalidEventException(string message) : base(message) { }
    public InvalidEventException(string message, Exception inner) : base(message, inner) { }
}

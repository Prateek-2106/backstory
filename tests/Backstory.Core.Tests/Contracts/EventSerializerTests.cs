using System.Text;
using Backstory.Core.Contracts;

namespace Backstory.Core.Tests.Contracts;

public class EventSerializerTests
{
    private static ArticlePublished SampleArticle() => new(
        ArticleId: "a-123",
        Headline: "Court blocks tariff ruling appeal",
        Summary: "An appeals court declined to pause the ruling.",
        Body: null,
        Url: "https://example-news.test/a-123",
        Section: "politics",
        PublishedAt: new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void RoundTrip_PreservesEnvelopeAndPayload()
    {
        var original = EventEnvelope<ArticlePublished>.Create("gateway-api", SampleArticle());

        var bytes = EventSerializer.Serialize(original);
        var parsed = EventSerializer.Deserialize<ArticlePublished>(bytes);

        Assert.Equal(original, parsed); // records compare by value
    }

    [Fact]
    public void Serialize_UsesCamelCaseTypeNameAndOmitsNulls()
    {
        var envelope = EventEnvelope<ArticlePublished>.Create("gateway-api", SampleArticle());

        var json = Encoding.UTF8.GetString(EventSerializer.Serialize(envelope));

        Assert.Contains("\"type\":\"backstory.articlepublished\"", json, StringComparison.Ordinal);
        Assert.Contains("\"articleId\":\"a-123\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"body\"", json, StringComparison.Ordinal); // Body was null
    }

    [Fact]
    public void Create_KeepsCorrelationIdFromCausingEvent()
    {
        var first = EventEnvelope<ArticlePublished>.Create("gateway-api", SampleArticle());

        var caused = EventEnvelope<ContextRequested>.Create(
            "context-worker", new ContextRequested("a-123", "cache-miss", Force: false), first.CorrelationId);

        Assert.Equal(first.CorrelationId, caused.CorrelationId);
        Assert.NotEqual(first.Id, caused.Id);
    }

    [Fact]
    public void Deserialize_WrongEventType_Throws()
    {
        var envelope = EventEnvelope<ContextRequested>.Create("gateway-api", new ContextRequested("a-1", "test", false));
        var bytes = EventSerializer.Serialize(envelope);

        var ex = Assert.Throws<InvalidEventException>(() => EventSerializer.Deserialize<ArticlePublished>(bytes));

        Assert.Contains("backstory.contextrequested", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("{\"id\":\"1\",\"type\":\"backstory.articlepublished\"}")] // no data
    public void Deserialize_MalformedMessage_Throws(string body)
    {
        Assert.Throws<InvalidEventException>(
            () => EventSerializer.Deserialize<ArticlePublished>(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void Deserialize_NewerSchemaVersion_Throws()
    {
        var future = EventEnvelope<ArticlePublished>.Create("gateway-api", SampleArticle()) with { SchemaVersion = 99 };

        Assert.Throws<InvalidEventException>(
            () => EventSerializer.Deserialize<ArticlePublished>(EventSerializer.Serialize(future)));
    }

    [Fact]
    public void Topics_AreVersionedAndHaveDeadLetters()
    {
        Assert.All(Topics.All, t => Assert.EndsWith(".v1", t, StringComparison.Ordinal));
        Assert.Equal("news.articles.v1.dlq", Topics.DeadLetter(Topics.Articles));
    }
}

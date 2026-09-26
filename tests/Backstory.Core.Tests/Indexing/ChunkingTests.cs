using Backstory.Core.Indexing;

namespace Backstory.Core.Tests.Indexing;

public class TextChunkerTests
{
    private static string Words(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));

    private static string Sentences(string tag, int count, int wordsEach) =>
        string.Join(' ', Enumerable.Range(1, count).Select(i => $"{tag}{i} " + Words("w", wordsEach - 2) + " end."));

    [Fact]
    public void ShortText_IsOneChunk()
    {
        var chunks = new TextChunker(chunkWords: 50).Chunk("First paragraph here.\n\nSecond paragraph here.");

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.Index);
        Assert.Equal("First paragraph here.\n\nSecond paragraph here.", chunk.Text); // paragraph break kept
    }

    [Fact]
    public void LongText_ChunksStayNearTarget_AndCoverEverything()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"P{i} " + Words("x", 29))); // 20 x 30 words

        var chunks = new TextChunker(chunkWords: 100, overlapWords: 30).Chunk(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.WordCount <= 100 + 40, $"chunk {c.Index} has {c.WordCount} words"));
        for (var i = 1; i <= 20; i++) // no paragraph lost
            Assert.Contains(chunks, c => c.Text.Contains($"P{i} ", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
    }

    [Fact]
    public void ConsecutiveChunks_Overlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"P{i} " + Words("x", 19))); // 10 x 20 words

        var chunks = new TextChunker(chunkWords: 60, overlapWords: 20).Chunk(text);

        // The last paragraph of one chunk is the first paragraph of the next.
        for (var i = 1; i < chunks.Count; i++)
        {
            var lastOfPrevious = chunks[i - 1].Text.Split("\n\n")[^1];
            Assert.StartsWith(lastOfPrevious, chunks[i].Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LongParagraph_IsSplitAtSentenceBoundaries_NeverMidSentence()
    {
        var paragraph = Sentences("S", count: 12, wordsEach: 20); // one 240-word paragraph

        var chunks = new TextChunker(chunkWords: 100, overlapWords: 0).Chunk(paragraph);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.EndsWith("end.", c.Text, StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.StartsWith("S", c.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void GiantSentenceWithoutPunctuation_IsCutByWords()
    {
        var chunks = new TextChunker(chunkWords: 100, overlapWords: 0).Chunk(Words("data", 350));

        Assert.All(chunks, c => Assert.True(c.WordCount <= 100));
        Assert.Equal(350, chunks.Sum(c => c.WordCount));
    }

    [Fact]
    public void TinyTail_IsMergedIntoPreviousChunk()
    {
        var text = "P1 " + Words("x", 99) + "\n\nTail with only a few words.";

        var chunks = new TextChunker(chunkWords: 100, overlapWords: 0, minChunkWords: 40).Chunk(text);

        var chunk = Assert.Single(chunks);
        Assert.EndsWith("Tail with only a few words.", chunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyText_NoChunks()
    {
        Assert.Empty(new TextChunker().Chunk("  \n\n  "));
    }
}

public class BoilerplateFilterTests
{
    [Fact]
    public void Clean_RemovesFurnitureDuplicatesAndLeadingTitle()
    {
        const string text = """
            World News in Brief

            The Security Council met on Tuesday to discuss the ceasefire.

            ♦ Receive daily updates directly in your inbox - Subscribe here to a topic.

            ♦ Download the UN News app for your iOS or Android devices.

            The Security Council met on Tuesday to discuss the ceasefire.

            Members agreed to subscribe to a joint statement on aid access.
            """;

        var cleaned = BoilerplateFilter.Clean(text, title: "World News in Brief");

        Assert.Equal(
            "The Security Council met on Tuesday to discuss the ceasefire.\n\nMembers agreed to subscribe to a joint statement on aid access.",
            cleaned.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}

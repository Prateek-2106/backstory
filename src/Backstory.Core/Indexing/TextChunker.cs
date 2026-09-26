using System.Text;
using System.Text.RegularExpressions;

namespace Backstory.Core.Indexing;

/// <summary>One piece of a document, small enough to embed and to quote as evidence.</summary>
public sealed record TextChunk(int Index, string Text, int WordCount);

/// <summary>
/// Splits a document into overlapping chunks of roughly <c>chunkWords</c> words.
///
///   1. Split into paragraphs; split long paragraphs into sentences; split monster sentences into words.
///      These "units" are the smallest pieces we never cut.
///   2. Fill a chunk with units until the next one would overflow it.
///   3. Start the next chunk with the last few units of the previous one (about <c>overlapWords</c>),
///      so a fact that straddles a boundary still appears whole in some chunk.
///   4. A tiny final chunk is merged into the previous one rather than stored alone.
///
/// Words, not model tokens, keep this dependency-free: ~250 English words ≈ 330 tokens, well inside
/// nomic-embed-text's 2048-token window.
/// </summary>
public sealed partial class TextChunker(int chunkWords = 250, int overlapWords = 40, int minChunkWords = 40)
{
    // A class (reference equality), not a record: two identical sentences must still count as different units.
    private sealed class Unit(string text, int words, int paragraph)
    {
        public string Text { get; } = text;
        public int Words { get; } = words;
        public int Paragraph { get; } = paragraph;
    }

    public IReadOnlyList<TextChunk> Chunk(string text)
    {
        var units = ToUnits(text);
        if (units.Count == 0) return [];

        var chunks = new List<List<Unit>>();
        var current = new List<Unit>();
        var currentWords = 0;

        foreach (var unit in units)
        {
            if (currentWords + unit.Words > chunkWords && current.Count > 0)
            {
                chunks.Add(current);
                current = Overlap(current);
                currentWords = current.Sum(u => u.Words);

                // Keep chunks near the target size: shed overlap if overlap + this unit would overflow.
                while (current.Count > 0 && currentWords + unit.Words > chunkWords)
                {
                    currentWords -= current[0].Words;
                    current.RemoveAt(0);
                }
            }
            current.Add(unit);
            currentWords += unit.Words;
        }

        // Last chunk: keep it only if it adds something beyond the overlap it started with.
        var lastIsOnlyOverlap = chunks.Count > 0 && current.All(u => chunks[^1].Contains(u));
        if (!lastIsOnlyOverlap)
        {
            if (chunks.Count > 0 && currentWords < minChunkWords)
                chunks[^1].AddRange(current.Where(u => !chunks[^1].Contains(u))); // too small alone: merge back
            else
                chunks.Add(current);
        }

        return chunks.Select((c, i) => new TextChunk(i, Join(c), c.Sum(u => u.Words))).ToList();
    }

    /// <summary>The trailing units of a full chunk that fit in the overlap budget (never the whole chunk).</summary>
    private List<Unit> Overlap(List<Unit> chunk)
    {
        var tail = new List<Unit>();
        var words = 0;
        for (var i = chunk.Count - 1; i > 0; i--) // i > 0: always leave at least one unit behind, so we make progress
        {
            if (words + chunk[i].Words > overlapWords) break;
            tail.Insert(0, chunk[i]);
            words += chunk[i].Words;
        }
        return tail;
    }

    private List<Unit> ToUnits(string text)
    {
        var units = new List<Unit>();
        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var p = 0; p < paragraphs.Length; p++)
        {
            var paragraph = Whitespace().Replace(paragraphs[p], " ");
            var words = CountWords(paragraph);
            if (words <= chunkWords)
            {
                units.Add(new Unit(paragraph, words, p));
                continue;
            }

            foreach (var sentence in SentenceBoundary().Split(paragraph).Where(s => s.Length > 0))
            {
                var sentenceWords = CountWords(sentence);
                if (sentenceWords <= chunkWords)
                {
                    units.Add(new Unit(sentence, sentenceWords, p));
                    continue;
                }

                // A "sentence" longer than a whole chunk (tables, lists without punctuation): cut by words.
                var all = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < all.Length; i += chunkWords)
                {
                    var piece = all.Skip(i).Take(chunkWords).ToArray();
                    units.Add(new Unit(string.Join(' ', piece), piece.Length, p));
                }
            }
        }
        return units;
    }

    // Units from the same paragraph are joined by a space, across paragraphs by a blank line.
    private static string Join(List<Unit> units)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < units.Count; i++)
        {
            if (i > 0) sb.Append(units[i].Paragraph == units[i - 1].Paragraph ? " " : "\n\n");
            sb.Append(units[i].Text);
        }
        return sb.ToString();
    }

    internal static int CountWords(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // After . ! ? (optionally followed by a closing quote or bracket), before whitespace + an uppercase letter/digit/quote.
    [GeneratedRegex(@"(?<=[.!?][""”’')\]]?)\s+(?=[A-Z0-9“""'(])", RegexOptions.None, 1000)]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Whitespace();
}

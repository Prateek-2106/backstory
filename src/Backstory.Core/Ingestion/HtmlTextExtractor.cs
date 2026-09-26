using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Backstory.Core.Ingestion;

/// <summary>Readable text pulled out of a web page.</summary>
public sealed record ExtractedPage(string? Title, string Text, DateTimeOffset? PublishedAt);

/// <summary>
/// Turns an article page into clean text for embedding. Deliberately simple and dependency-free:
///   1. throw away code and chrome: script, style, nav, header, footer, aside, forms...
///   2. keep the main region: the &lt;article&gt; or &lt;main&gt; with the most paragraph text, else &lt;body&gt;
///   3. split on block tags (p, li, h1-h6...), strip remaining tags, decode entities
///   4. drop short lines ("Share", "Menu", "Read more"), which are almost always navigation
/// Trade-off: regexes are not a real HTML parser and will occasionally keep junk or drop a line.
/// For RAG that is acceptable; the grounding check later guards what readers see.
/// </summary>
public static partial class HtmlTextExtractor
{
    private const int MinWordsPerLine = 6;
    private const int MaxTextChars = 100_000;
    private const int MinRegionChars = 200;

    public static ExtractedPage Extract(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var metas = ReadMetaTags(html);

        // Prefer og:title (the clean headline) over <title> (often "Headline | Site Name").
        var rawTitle = metas.GetValueOrDefault("og:title");
        if (rawTitle is null && TitleTag().Match(html) is { Success: true } titleTag)
            rawTitle = titleTag.Groups[1].Value;
        var title = rawTitle is null ? null : ToPlainText(rawTitle);

        DateTimeOffset? published = null;
        if (metas.GetValueOrDefault("article:published_time") is { } iso &&
            DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p))
            published = p;

        var body = RemoveNoise(html);
        var text = BestText(body);

        return new ExtractedPage(string.IsNullOrWhiteSpace(title) ? null : title, text, published);
    }

    /// <summary>Strip all tags from a small fragment (a feed title or summary) and tidy whitespace.</summary>
    public static string ToPlainText(string fragment)
    {
        var noTags = AnyTag().Replace(fragment, " ");
        return Whitespace().Replace(WebUtility.HtmlDecode(noTags), " ").Trim();
    }

    private static string RemoveNoise(string html)
    {
        var s = Comments().Replace(html, " ");
        s = NoiseBlocks().Replace(s, " ");
        return s;
    }

    /// <summary>
    /// Try each &lt;article&gt; and &lt;main&gt; region and keep the one that yields the most paragraph text
    /// (not the most raw HTML: a card grid of teasers is large but says little). If none yields a real
    /// article's worth of text, fall back to the whole &lt;body&gt;.
    /// </summary>
    private static string BestText(string html)
    {
        var best = BalancedBlocks(html, "article")
            .Concat(BalancedBlocks(html, "main"))
            .Select(ToParagraphs)
            .OrderByDescending(t => t.Length)
            .FirstOrDefault() ?? "";

        if (best.Length >= MinRegionChars)
            return best;

        var bodyRegion = BodyBlock().Match(html) is { Success: true } b ? b.Groups[1].Value : html;
        var whole = ToParagraphs(bodyRegion);
        return whole.Length > best.Length ? whole : best;
    }

    /// <summary>
    /// The contents of each OUTERMOST &lt;tag&gt;...&lt;/tag&gt;, counting nesting properly.
    /// A plain non-greedy regex stops at the first closing tag, so for
    /// &lt;article&gt; lead &lt;article&gt;image&lt;/article&gt; body &lt;/article&gt; it would return only "lead + image".
    /// (That bug truncated UN News stories, which nest an &lt;article&gt; for each embedded image.)
    /// </summary>
    internal static IEnumerable<string> BalancedBlocks(string html, string tag)
    {
        var depth = 0;
        var start = 0;
        foreach (Match m in RegionTag().Matches(html))
        {
            if (!m.Groups[2].Value.Equals(tag, StringComparison.OrdinalIgnoreCase))
                continue;

            if (m.Groups[1].Value != "/")
            {
                if (depth == 0) start = m.Index + m.Length;
                depth++;
            }
            else if (depth > 0 && --depth == 0)
            {
                yield return html[start..m.Index];
            }
        }
    }

    private static string ToParagraphs(string region)
    {
        var withBreaks = BlockTag().Replace(region, "\n");
        var noTags = AnyTag().Replace(withBreaks, " ");
        var decoded = WebUtility.HtmlDecode(noTags);

        var sb = new StringBuilder();
        foreach (var raw in decoded.Split('\n'))
        {
            var line = Whitespace().Replace(raw, " ").Trim();
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < MinWordsPerLine)
                continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(line);
            if (sb.Length >= MaxTextChars) break;
        }

        return sb.Length > MaxTextChars ? sb.ToString(0, MaxTextChars) : sb.ToString();
    }

    private static Dictionary<string, string> ReadMetaTags(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in MetaTag().Matches(html))
        {
            var attrs = Attribute().Matches(tag.Value)
                .ToDictionary(a => a.Groups[1].Value.ToLowerInvariant(), a => a.Groups[3].Value, StringComparer.Ordinal);
            var key = attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name");
            if (key is not null && attrs.TryGetValue("content", out var content))
                result.TryAdd(key, WebUtility.HtmlDecode(content));
        }
        return result;
    }

    // Source-generated regexes: compiled at build time, with a timeout so a hostile page can't hang us.
    private const int TimeoutMs = 1000;
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions OptsMulti = Opts | RegexOptions.Singleline;

    [GeneratedRegex("<!--.*?-->", OptsMulti, TimeoutMs)] private static partial Regex Comments();
    [GeneratedRegex(@"<(script|style|noscript|svg|iframe|template|head|nav|header|footer|aside|form|button|figure)\b[^>]*>.*?</\1\s*>", OptsMulti, TimeoutMs)] private static partial Regex NoiseBlocks();
    [GeneratedRegex(@"<(/?)(article|main)\b[^>]*>", Opts, TimeoutMs)] private static partial Regex RegionTag();
    [GeneratedRegex(@"<body\b[^>]*>(.*?)</body\s*>", OptsMulti, TimeoutMs)] private static partial Regex BodyBlock();
    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", OptsMulti, TimeoutMs)] private static partial Regex TitleTag();
    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|td|th|section|blockquote|pre|table)\b[^>]*>", Opts, TimeoutMs)] private static partial Regex BlockTag();
    [GeneratedRegex(@"<[^>]+>", Opts, TimeoutMs)] private static partial Regex AnyTag();
    [GeneratedRegex(@"<meta\b[^>]*>", Opts, TimeoutMs)] private static partial Regex MetaTag();
    [GeneratedRegex(@"([\w:-]+)\s*=\s*([""'])(.*?)\2", OptsMulti, TimeoutMs)] private static partial Regex Attribute();
    [GeneratedRegex(@"\s+", RegexOptions.None, TimeoutMs)] private static partial Regex Whitespace();
}

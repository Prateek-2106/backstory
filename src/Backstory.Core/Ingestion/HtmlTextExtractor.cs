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
///   2. keep the main region: the largest &lt;article&gt;, else &lt;main&gt;, else &lt;body&gt;
///   3. split on block tags (p, li, h1-h6...), strip remaining tags, decode entities
///   4. drop short lines ("Share", "Menu", "Read more"), which are almost always navigation
/// Trade-off: regexes are not a real HTML parser and will occasionally keep junk or drop a line.
/// For RAG that is acceptable; the grounding check later guards what readers see.
/// </summary>
public static partial class HtmlTextExtractor
{
    private const int MinWordsPerLine = 6;
    private const int MaxTextChars = 100_000;

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
        var region = PickMainRegion(body);
        var text = ToParagraphs(region);

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

    private static string PickMainRegion(string html)
    {
        var largestArticle = ArticleBlock().Matches(html)
            .Select(m => m.Groups[1].Value)
            .OrderByDescending(v => v.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(largestArticle))
            return largestArticle;

        if (MainBlock().Match(html) is { Success: true } main)
            return main.Groups[1].Value;

        return BodyBlock().Match(html) is { Success: true } body ? body.Groups[1].Value : html;
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
    [GeneratedRegex(@"<article\b[^>]*>(.*?)</article\s*>", OptsMulti, TimeoutMs)] private static partial Regex ArticleBlock();
    [GeneratedRegex(@"<main\b[^>]*>(.*?)</main\s*>", OptsMulti, TimeoutMs)] private static partial Regex MainBlock();
    [GeneratedRegex(@"<body\b[^>]*>(.*?)</body\s*>", OptsMulti, TimeoutMs)] private static partial Regex BodyBlock();
    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", OptsMulti, TimeoutMs)] private static partial Regex TitleTag();
    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|td|th|section|blockquote|pre|table)\b[^>]*>", Opts, TimeoutMs)] private static partial Regex BlockTag();
    [GeneratedRegex(@"<[^>]+>", Opts, TimeoutMs)] private static partial Regex AnyTag();
    [GeneratedRegex(@"<meta\b[^>]*>", Opts, TimeoutMs)] private static partial Regex MetaTag();
    [GeneratedRegex(@"([\w:-]+)\s*=\s*([""'])(.*?)\2", OptsMulti, TimeoutMs)] private static partial Regex Attribute();
    [GeneratedRegex(@"\s+", RegexOptions.None, TimeoutMs)] private static partial Regex Whitespace();
}

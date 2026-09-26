using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Backstory.Core.Ingestion;

/// <summary>One entry from an RSS or Atom feed.</summary>
public sealed record FeedItem(string Title, string Link, string? Summary, DateTimeOffset? PublishedAt);

/// <summary>Reads RSS 2.0, RSS 1.0 (RDF) and Atom feeds into <see cref="FeedItem"/>s.</summary>
public static class FeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    /// <param name="feedUrl">Used to resolve relative links like "/news/123".</param>
    public static IReadOnlyList<FeedItem> Parse(string xml, Uri feedUrl)
    {
        var doc = Load(xml);
        var root = doc.Root ?? throw new FeedFormatException("Feed has no root element.");

        return root.Name == Atom + "feed"
            ? ParseAtom(root, feedUrl)
            : ParseRss(root, feedUrl);
    }

    private static XDocument Load(string xml)
    {
        // Feeds come from the internet: never process DTDs (blocks "billion laughs" entity bombs
        // and external entity tricks).
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        try
        {
            // Tolerate a stray byte-order mark or whitespace before "<?xml" (both are common on real feeds).
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FeedFormatException($"Feed is not valid XML: {ex.Message}", ex);
        }
    }

    // RSS 2.0 (<rss><channel><item>) and RSS 1.0 (<rdf:RDF><item>): match by local name so namespaces don't matter.
    private static List<FeedItem> ParseRss(XElement root, Uri feedUrl)
    {
        var items = new List<FeedItem>();
        foreach (var item in root.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var title = Child(item, "title");
            var link = Child(item, "link");
            if (string.IsNullOrWhiteSpace(title) || !TryResolve(link, feedUrl, out var url))
                continue; // an item without a title or link is useless to us

            var summary = Child(item, "description");
            var date = ParseDate(Child(item, "pubDate") ?? Child(item, "date")); // "date" = Dublin Core in RSS 1.0
            items.Add(new FeedItem(Clean(title), url, CleanOrNull(summary), date));
        }
        return items;
    }

    private static List<FeedItem> ParseAtom(XElement root, Uri feedUrl)
    {
        var items = new List<FeedItem>();
        foreach (var entry in root.Elements(Atom + "entry"))
        {
            var title = entry.Element(Atom + "title")?.Value;
            // Atom can have several links; the article is rel="alternate" (or no rel at all).
            var link = entry.Elements(Atom + "link")
                .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")
                ?.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(title) || !TryResolve(link, feedUrl, out var url))
                continue;

            var summary = entry.Element(Atom + "summary")?.Value ?? entry.Element(Atom + "content")?.Value;
            var date = ParseDate(entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value);
            items.Add(new FeedItem(Clean(title), url, CleanOrNull(summary), date));
        }
        return items;
    }

    private static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static bool TryResolve(string? link, Uri feedUrl, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(feedUrl, link.Trim(), out var resolved))
            return false;
        url = resolved.AbsoluteUri;
        return true;
    }

    // Titles and summaries often contain HTML (<p>, &amp;): reduce to plain text.
    private static string Clean(string s) => HtmlTextExtractor.ToPlainText(s);

    private static string? CleanOrNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var text = Clean(s);
        return text.Length == 0 ? null : text;
    }

    // RSS uses RFC 822 dates ("Sat, 26 Sep 2026 14:05:00 EDT"). .NET understands numeric offsets
    // and GMT, but not US zone names, so swap those for offsets first.
    private static readonly Dictionary<string, string> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+0000", ["UTC"] = "+0000", ["GMT"] = "+0000", ["Z"] = "+0000",
        ["EST"] = "-0500", ["EDT"] = "-0400", ["CST"] = "-0600", ["CDT"] = "-0500",
        ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
    };

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();

        var lastSpace = s.LastIndexOf(' ');
        if (lastSpace > 0 && ZoneOffsets.TryGetValue(s[(lastSpace + 1)..], out var offset))
            s = s[..lastSpace] + " " + offset;

        string[] formats =
        [
            "ddd, d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz",
            "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss",
        ];
        // "zzz" wants "+00:00"; RFC 822 writes "+0000". Insert the colon.
        var withColon = System.Text.RegularExpressions.Regex.Replace(s, @"([+-]\d\d)(\d\d)$", "$1:$2");

        if (DateTimeOffset.TryParseExact(withColon, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var exact))
            return exact;

        // ISO 8601 (Atom, Dublin Core) and anything else .NET can read.
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}

public sealed class FeedFormatException : Exception
{
    public FeedFormatException() { }
    public FeedFormatException(string message) : base(message) { }
    public FeedFormatException(string message, Exception inner) : base(message, inner) { }
}

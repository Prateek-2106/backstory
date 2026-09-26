using System.Text.RegularExpressions;

namespace Backstory.Core.Indexing;

/// <summary>
/// Removes paragraphs that are site furniture rather than content ("Subscribe here", "Download the app",
/// "Follow us on..."), repeated paragraphs, and a leading copy of the title. They would otherwise be
/// embedded and could even be retrieved as "evidence".
/// </summary>
public static partial class BoilerplateFilter
{
    public static string Clean(string text, string? title = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0) continue;
            if (Boilerplate().IsMatch(paragraph)) continue;
            if (title is not null && string.Equals(paragraph, title.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(paragraph)) continue; // duplicate paragraph

            kept.Add(paragraph);
        }

        return string.Join("\n\n", kept);
    }

    // Anchored at the start of a paragraph (after an optional bullet like ♦ • ►), so ordinary sentences
    // that merely contain "subscribe" or "copyright" somewhere are kept.
    [GeneratedRegex(
        @"^[\s♦•►▶·*-]*(receive (daily )?updates|subscribe|sign up|download (the|our)|get the .* app|follow us|share (this|on)|read more|click here|related:|advertisement|listen to|watch:|photo:|image:|©|copyright|all rights reserved)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Boilerplate();
}

using System.Text.RegularExpressions;

namespace Backstory.Core.Ingestion;

/// <summary>
/// Minimal robots.txt reader: the rules a site publishes for crawlers.
/// Uses the group for our user-agent token if present, else the "*" group.
/// The longest matching rule wins; on a tie, Allow wins (same as Google's crawler).
/// Supports "*" (any characters) and "$" (end of path).
/// </summary>
public sealed class RobotsTxt
{
    public static readonly RobotsTxt AllowAll = new([]);

    private readonly IReadOnlyList<(bool Allow, string Pattern, Regex Regex)> _rules;

    private RobotsTxt(IReadOnlyList<(bool, string, Regex)> rules) => _rules = rules;

    public static RobotsTxt Parse(string content, string userAgentToken)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        List<string>? currentAgents = null;
        List<(bool, string)>? currentRules = null;
        var lastWasAgent = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (field == "user-agent")
            {
                if (!lastWasAgent) // a new group starts
                {
                    currentAgents = [];
                    currentRules = [];
                    groups.Add((currentAgents, currentRules));
                }
                currentAgents!.Add(value.ToLowerInvariant());
                lastWasAgent = true;
            }
            else if (field is "allow" or "disallow" && currentRules is not null)
            {
                lastWasAgent = false;
                if (value.Length == 0) continue; // "Disallow:" with nothing = allow everything
                currentRules.Add((field == "allow", value));
            }
            else
            {
                lastWasAgent = false;
            }
        }

        var token = userAgentToken.ToLowerInvariant();
        var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && token.Contains(a, StringComparison.Ordinal)));
        if (group.Rules is null)
            group = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        if (group.Rules is null)
            return AllowAll;

        return new RobotsTxt(group.Rules.Select(r => (r.Item1, r.Item2, ToRegex(r.Item2))).ToList());
    }

    public bool IsAllowed(string pathAndQuery)
    {
        (bool Allow, int Length)? best = null;
        foreach (var (allow, pattern, regex) in _rules)
        {
            if (!regex.IsMatch(pathAndQuery)) continue;
            if (best is null || pattern.Length > best.Value.Length || (pattern.Length == best.Value.Length && allow))
                best = (allow, pattern.Length);
        }
        return best?.Allow ?? true;
    }

    private static Regex ToRegex(string pattern)
    {
        var anchoredEnd = pattern.EndsWith('$');
        var body = anchoredEnd ? pattern[..^1] : pattern;
        var regex = "^" + Regex.Escape(body).Replace(@"\*", ".*", StringComparison.Ordinal) + (anchoredEnd ? "$" : "");
        return new Regex(regex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}

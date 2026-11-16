namespace Jellyfin.Plugin.Trailhead.Dns;

/// <summary>
/// The Public Suffix List audit. The weekly workflow feeds it the live list; the unit tests feed it a pinned copy.
/// </summary>
internal static class PublicSuffixList
{
    /// <summary>The exact rules of a list: no comments, no wildcard or exception rules, lower-cased.</summary>
    internal static HashSet<string> ParseRules(string? text)
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();

            // Only exact parents are ever asked about, so wildcard (*.x) and exception (!x) rules are left out: a
            // wildcard on x doesn't make x itself a public suffix.
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line[0] is '*' or '!')
            {
                continue;
            }

            // A rule ends at the first whitespace; the spec allows trailing text.
            var end = line.IndexOfAny(new[] { ' ', '\t' });
            rules.Add((end < 0 ? line : line[..end]).ToLowerInvariant());
        }

        return rules;
    }

    /// <summary>
    /// The offered parents that are missing from the list. A list with fewer than <paramref name="minRules"/> rules is an
    /// error, not a pass: a truncated or redirected download would otherwise audit nothing, or fail everything and read
    /// as a catastrophe instead of a bad download.
    /// </summary>
    internal static IReadOnlyList<string> MissingParents(ParentDomains catalogue, string? listText, int minRules)
    {
        var rules = ParseRules(listText);
        if (rules.Count == 0 || rules.Count < minRules)
        {
            throw new InvalidDataException($"The Public Suffix List looks wrong: {rules.Count} rules, expected at least {Math.Max(minRules, 1)}.");
        }

        return catalogue.Offered.Where(p => !rules.Contains(p.Name)).Select(p => p.Name).ToList();
    }
}

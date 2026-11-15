namespace Jellyfin.Plugin.Trailhead.Dns;

public enum LabelProblem
{
    None,
    Empty,
    TooShort,
    TooLong,
    HasDot,
    BadCharacters,
    EdgeDash,
    ReservedPrefix,
}

internal sealed record LabelCheck(string Label, LabelProblem Problem, string? Message)
{
    internal bool Ok => Problem == LabelProblem.None;
}

internal sealed record BuiltName(string? Name, string? Problem)
{
    internal bool Ok => Name is not null;
}

/// <summary>The name part of a hostname: one DNS label, checked and lower-cased.</summary>
internal static class HostLabel
{
    internal const int MinLength = 3;

    internal const int MaxLength = 63;

    // The rules are DNS's, plus our own floor of 3: one- and two-character names are long gone on these parents, and
    // finding that out through a failed create is worse than saying so up front.
    internal static LabelCheck Check(string? label)
    {
        var text = (label ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        if (text.Length == 0)
        {
            return Fail(lower, LabelProblem.Empty, "Type a name.");
        }

        if (text.Length > MaxLength)
        {
            return Fail(lower, LabelProblem.TooLong, $"Use {MaxLength} characters or fewer.");
        }

        // Before the character check: a pasted full address should be told about the dots, not just "bad characters".
        if (text.Contains('.'))
        {
            return Fail(lower, LabelProblem.HasDot, "Use one word, with no dots.");
        }

        // ASCII only, spelled out: char.IsLetter would let Cyrillic look-alikes through.
        if (!text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
        {
            return Fail(lower, LabelProblem.BadCharacters, "Use only letters, numbers and dashes.");
        }

        if (text[0] == '-' || text[^1] == '-')
        {
            return Fail(lower, LabelProblem.EdgeDash, "A name can't start or end with a dash.");
        }

        // "xn--" marks encoded Unicode; a label that starts with it and isn't, resolves nowhere and nothing says why.
        if (lower.StartsWith("xn--", StringComparison.Ordinal))
        {
            return Fail(lower, LabelProblem.ReservedPrefix, "A name can't start with xn--.");
        }

        if (text.Length < MinLength)
        {
            return Fail(lower, LabelProblem.TooShort, $"Use at least {MinLength} characters.");
        }

        return new LabelCheck(lower, LabelProblem.None, null);
    }

    internal static BuiltName Build(string? label, string? parent, ParentDomains? catalogue = null)
    {
        var check = Check(label);
        if (!check.Ok)
        {
            return new BuiltName(null, check.Message);
        }

        catalogue ??= ParentDomains.Embedded();
        if (!catalogue.Allows(parent))
        {
            var reason = catalogue.BlockedReason(parent);
            return new BuiltName(null, reason is null ? "That ending isn't offered." : "That ending can't be used: " + reason + ".");
        }

        return new BuiltName(check.Label + "." + ParentDomains.Normalize(parent), null);
    }

    private static LabelCheck Fail(string label, LabelProblem problem, string message) => new(label, problem, message);
}

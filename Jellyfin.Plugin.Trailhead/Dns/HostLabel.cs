using Campground.Shared.I18n;

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

    /// <summary>The parent isn't in the list Trailhead offers. Set by <see cref="HostLabel.Build"/>, not by the label check.</summary>
    ParentNotOffered,

    /// <summary>The parent is on the refused list (it has a reason, which comes with the answer).</summary>
    ParentBlocked,
}

internal sealed record LabelCheck(string Label, LabelProblem Problem, string? Message)
{
    internal bool Ok => Problem == LabelProblem.None;
}

/// <summary>A built name, or why not. <see cref="Message"/> is the English text; callers that translate use <see cref="Problem"/>.</summary>
internal sealed record BuiltName(string? Name, string? Message, LabelProblem Problem = LabelProblem.None, string? Reason = null)
{
    internal bool Ok => Name is not null;
}

/// <summary>The name part of a hostname: one DNS label, checked and lower-cased.</summary>
internal static class HostLabel
{
    internal const int MinLength = 3;

    internal const int MaxLength = 63;

    /// <summary>The string-table key for a problem, e.g. <c>label.tooShort</c>.</summary>
    internal static string KeyFor(LabelProblem problem) => "label." + char.ToLowerInvariant(problem.ToString()[0]) + problem.ToString()[1..];

    /// <summary>The words for a problem in one language. <paramref name="reason"/> fills <see cref="LabelProblem.ParentBlocked"/>.</summary>
    internal static string Describe(Strings strings, LabelProblem problem, string? reason = null, string? language = null)
        => strings.Get(KeyFor(problem), language, new Dictionary<string, object?> { ["min"] = MinLength, ["max"] = MaxLength, ["reason"] = reason });

    // The rules are DNS's, plus our own floor of 3: one- and two-character names are long gone on these parents, and
    // finding that out through a failed create is worse than saying so up front.
    internal static LabelCheck Check(string? label)
    {
        var text = (label ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();

        if (text.Length == 0)
        {
            return Fail(lower, LabelProblem.Empty);
        }

        if (text.Length > MaxLength)
        {
            return Fail(lower, LabelProblem.TooLong);
        }

        // Before the character check: a pasted full address should be told about the dots, not just "bad characters".
        if (text.Contains('.'))
        {
            return Fail(lower, LabelProblem.HasDot);
        }

        // ASCII only, spelled out: char.IsLetter would let Cyrillic look-alikes through.
        if (!text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
        {
            return Fail(lower, LabelProblem.BadCharacters);
        }

        if (text[0] == '-' || text[^1] == '-')
        {
            return Fail(lower, LabelProblem.EdgeDash);
        }

        // "xn--" marks encoded Unicode; a label that starts with it and isn't, resolves nowhere and nothing says why.
        if (lower.StartsWith("xn--", StringComparison.Ordinal))
        {
            return Fail(lower, LabelProblem.ReservedPrefix);
        }

        if (text.Length < MinLength)
        {
            return Fail(lower, LabelProblem.TooShort);
        }

        return new LabelCheck(lower, LabelProblem.None, null);
    }

    internal static BuiltName Build(string? label, string? parent, ParentDomains? catalogue = null)
    {
        var check = Check(label);
        if (!check.Ok)
        {
            return new BuiltName(null, check.Message, check.Problem);
        }

        catalogue ??= ParentDomains.Embedded();
        if (!catalogue.Allows(parent))
        {
            var reason = catalogue.BlockedReason(parent);
            var problem = reason is null ? LabelProblem.ParentNotOffered : LabelProblem.ParentBlocked;
            return new BuiltName(null, Describe(UiText.Table, problem, reason, Strings.Fallback), problem, reason);
        }

        return new BuiltName(check.Label + "." + ParentDomains.Normalize(parent), null);
    }

    private static LabelCheck Fail(string label, LabelProblem problem) => new(label, problem, Describe(UiText.Table, problem, null, Strings.Fallback));
}

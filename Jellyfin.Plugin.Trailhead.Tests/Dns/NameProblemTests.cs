using Jellyfin.Plugin.Trailhead.Dns;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

/// <summary>The kinds and problems the page translates, and the English text that now comes out of the string table.</summary>
public sealed class NameProblemTests
{
    private static readonly ParentDomains Parents = ParentDomains.Embedded();

    [Theory]
    [InlineData("", "freeddns.org", LabelProblem.Empty)]
    [InlineData("ab", "freeddns.org", LabelProblem.TooShort)]
    [InlineData("a.b.c", "freeddns.org", LabelProblem.HasDot)]
    [InlineData("ab_cd", "freeddns.org", LabelProblem.BadCharacters)]
    [InlineData("-abc", "freeddns.org", LabelProblem.EdgeDash)]
    [InlineData("xn--abc", "freeddns.org", LabelProblem.ReservedPrefix)]
    [InlineData("abc", "example.com", LabelProblem.ParentNotOffered)]
    [InlineData("abc", "dynu.com", LabelProblem.ParentBlocked)]
    public void An_unusable_name_is_Invalid_and_says_which_rule_it_broke(string label, string parent, LabelProblem problem)
    {
        var invalid = NameClaimer.Validate(label, parent, Parents);

        Assert.NotNull(invalid);
        Assert.Equal(ClaimKind.Invalid, invalid!.Kind);
        Assert.Equal(problem, invalid.Problem);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
        Assert.Equal(problem == LabelProblem.ParentBlocked, invalid.Reason is not null);
    }

    [Fact]
    public void A_usable_name_validates_to_nothing()
    {
        Assert.Null(NameClaimer.Validate("mymedia", "freeddns.org", Parents));
    }

    [Fact]
    public void The_English_text_comes_from_the_table_with_its_numbers_filled_in()
    {
        Assert.Equal("Use at least 3 characters.", HostLabel.Check("ab").Message);
        Assert.Equal("Use 63 characters or fewer.", HostLabel.Check(new string('a', 64)).Message);
        Assert.Equal("Someone else has that name. Try it with another ending.", UiText.English(NameClaimer.KeyFor(ClaimKind.Taken)));
        Assert.Equal("Re-enter your Dynu key.", Jellyfin.Plugin.Trailhead.Dns.DynuKey.ReenterMessage);
    }

    [Fact]
    public void Every_kind_and_problem_has_a_distinct_key()
    {
        var kinds = Enum.GetValues<ClaimKind>().Select(NameClaimer.KeyFor).ToList();
        var problems = Enum.GetValues<LabelProblem>().Where(p => p != LabelProblem.None).Select(HostLabel.KeyFor).ToList();

        Assert.NotEmpty(kinds);
        Assert.NotEmpty(problems);
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
        Assert.Equal(problems.Count, problems.Distinct().Count());
    }
}

using Jellyfin.Plugin.Trailhead.Dns;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class HostLabelTests
{
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("Home-Server-1", "home-server-1")]
    [InlineData("  MyMedia  ", "mymedia")]
    [InlineData("a-b", "a-b")]
    [InlineData("123", "123")]
    public void A_good_label_is_accepted_and_lower_cased(string input, string expected)
    {
        var check = HostLabel.Check(input);

        Assert.True(check.Ok, check.Message);
        Assert.Equal(expected, check.Label);
        Assert.Null(check.Message);
    }

    [Fact]
    public void Sixty_three_characters_is_the_longest_allowed()
    {
        Assert.True(HostLabel.Check(new string('a', 63)).Ok);
        Assert.Equal(LabelProblem.TooLong, HostLabel.Check(new string('a', 64)).Problem);
    }

    [Theory]
    [InlineData(null, LabelProblem.Empty)]
    [InlineData("", LabelProblem.Empty)]
    [InlineData("   ", LabelProblem.Empty)]
    [InlineData("ab", LabelProblem.TooShort)]
    [InlineData("a", LabelProblem.TooShort)]
    [InlineData("my.media", LabelProblem.HasDot)]
    [InlineData("mymedia.freeddns.org", LabelProblem.HasDot)]
    [InlineData("a.b", LabelProblem.HasDot)]
    [InlineData(".abc", LabelProblem.HasDot)]
    [InlineData("abc.", LabelProblem.HasDot)]
    [InlineData("my media", LabelProblem.BadCharacters)]
    [InlineData("my_media", LabelProblem.BadCharacters)]
    [InlineData("cafés", LabelProblem.BadCharacters)]
    [InlineData("аbc", LabelProblem.BadCharacters)]
    [InlineData("abc/def", LabelProblem.BadCharacters)]
    [InlineData("-abc", LabelProblem.EdgeDash)]
    [InlineData("abc-", LabelProblem.EdgeDash)]
    [InlineData("xn--abc", LabelProblem.ReservedPrefix)]
    [InlineData("XN--abc", LabelProblem.ReservedPrefix)]
    public void A_bad_label_gets_its_own_reason(string? input, LabelProblem expected)
    {
        var check = HostLabel.Check(input);

        Assert.False(check.Ok);
        Assert.Equal(expected, check.Problem);
        Assert.False(string.IsNullOrWhiteSpace(check.Message));
    }

    [Fact]
    public void Every_problem_has_a_different_message()
    {
        var inputs = new[] { string.Empty, "ab", new string('a', 64), "a.bc", "a b", "-abc", "xn--abc" };

        var messages = inputs.Select(i => HostLabel.Check(i).Message).ToList();

        Assert.Equal(inputs.Length, messages.Distinct().Count());
        Assert.All(messages, m => Assert.True(m!.Length < 60, m));
    }

    [Fact]
    public void Build_joins_the_label_and_an_offered_parent()
    {
        var built = HostLabel.Build("MyMedia", " FreeDDNS.org ");

        Assert.True(built.Ok);
        Assert.Equal("mymedia.freeddns.org", built.Name);
    }

    [Theory]
    [InlineData("abc", "dynu.com")]
    [InlineData("abc", "example.com")]
    [InlineData("abc", "")]
    [InlineData("abc", null)]
    [InlineData("abc", "org")]
    [InlineData("abc", "sub.freeddns.org")]
    public void Build_refuses_a_parent_that_is_not_offered(string label, string? parent)
    {
        var built = HostLabel.Build(label, parent);

        Assert.False(built.Ok);
        Assert.Null(built.Name);
        Assert.False(string.IsNullOrWhiteSpace(built.Problem));
    }

    [Fact]
    public void Build_gives_the_blocked_reason_for_a_blocked_parent()
    {
        var built = HostLabel.Build("abc", "dynu.com");

        Assert.Contains("Public Suffix List", built.Problem);
    }

    [Theory]
    [InlineData("", "freeddns.org")]
    [InlineData("a.b", "freeddns.org")]
    [InlineData("ab", "freeddns.org")]
    public void Build_refuses_a_bad_label_with_the_labels_reason(string label, string parent)
    {
        var built = HostLabel.Build(label, parent);

        Assert.False(built.Ok);
        Assert.Equal(HostLabel.Check(label).Message, built.Problem);
    }
}

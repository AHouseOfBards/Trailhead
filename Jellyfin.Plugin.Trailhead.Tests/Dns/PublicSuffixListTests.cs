using Jellyfin.Plugin.Trailhead.Dns;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class PublicSuffixListTests
{
    private const string Small = "// comment\n\ncom\nfreeddns.org\n*.wild.test\n!x.wild.test\n  MyWire.ORG  \n";

    private static string Pinned => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "public_suffix_list.excerpt.dat"));

    [Fact]
    public void Rules_skip_comments_blanks_wildcards_and_exceptions_and_lower_case_the_rest()
    {
        var rules = PublicSuffixList.ParseRules(Small);

        Assert.Equal(new[] { "com", "freeddns.org", "mywire.org" }, rules.OrderBy(r => r));
    }

    [Fact]
    public void Rules_accept_windows_line_endings()
    {
        var rules = PublicSuffixList.ParseRules("com\r\nfreeddns.org\r\n");

        Assert.Contains("freeddns.org", rules);
        Assert.Contains("com", rules);
    }

    [Fact]
    public void A_parent_missing_from_the_list_is_reported()
    {
        var parents = ParentDomains.Parse("""{"domains":[{"name":"freeddns.org","rank":1,"default":true},{"name":"not-listed.example","rank":2}]}""");

        var missing = PublicSuffixList.MissingParents(parents, Small, minRules: 2);

        Assert.Equal(new[] { "not-listed.example" }, missing);
    }

    [Fact]
    public void A_parent_that_is_only_a_subdomain_of_a_listed_one_is_missing()
    {
        // "sub.freeddns.org" is under a listed suffix, but it is not itself a public suffix, so it shares nothing.
        var parents = ParentDomains.Parse("""{"domains":[{"name":"sub.freeddns.org","rank":1,"default":true}]}""");

        Assert.Equal(new[] { "sub.freeddns.org" }, PublicSuffixList.MissingParents(parents, Small, minRules: 2));
    }

    [Fact]
    public void A_wildcard_rule_does_not_count_as_listing_its_base()
    {
        var parents = ParentDomains.Parse("""{"domains":[{"name":"wild.test","rank":1,"default":true}]}""");

        Assert.Equal(new[] { "wild.test" }, PublicSuffixList.MissingParents(parents, Small, minRules: 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("// only a comment\n")]
    public void An_empty_list_is_an_error_not_a_pass(string? text)
    {
        Assert.Throws<InvalidDataException>(() => PublicSuffixList.MissingParents(ParentDomains.Embedded(), text, minRules: 1));
    }

    [Fact]
    public void A_list_shorter_than_the_minimum_is_an_error()
    {
        Assert.Throws<InvalidDataException>(() => PublicSuffixList.MissingParents(ParentDomains.Embedded(), Pinned, minRules: 5000));
    }

    [Fact]
    public void The_pinned_list_without_one_parent_reports_that_parent()
    {
        var without = string.Join('\n', Pinned.Split('\n').Where(l => l.Trim() != "opik.net"));

        var missing = PublicSuffixList.MissingParents(ParentDomains.Embedded(), without, minRules: 10);

        Assert.Equal(new[] { "opik.net" }, missing);
    }

    // Set by the weekly audit workflow to the freshly downloaded list. Without it this test has nothing to check and
    // passes; the workflow makes sure it was set and that the list is real.
    [Fact]
    public void Live_Public_Suffix_List_has_every_offered_parent()
    {
        var path = Environment.GetEnvironmentVariable("TRAILHEAD_PSL_FILE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var missing = PublicSuffixList.MissingParents(ParentDomains.Embedded(), File.ReadAllText(path), minRules: 5000);

        Assert.True(missing.Count == 0, "Offered but not on the Public Suffix List (every user on them would share one Let's Encrypt bucket): " + string.Join(", ", missing));
    }
}

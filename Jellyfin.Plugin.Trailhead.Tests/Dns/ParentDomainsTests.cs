using Jellyfin.Plugin.Trailhead.Dns;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class ParentDomainsTests
{
    private static readonly string[] Unsafe = { "flashhub.net", "ezgateway.net", "remotewire.net", "dynu.com", "dynu.org" };

    [Fact]
    public void The_embedded_catalogue_offers_nine_parents_with_exactly_one_default()
    {
        var parents = ParentDomains.Embedded();

        Assert.Equal(9, parents.Offered.Count);
        Assert.Equal("freeddns.org", Assert.Single(parents.Offered, p => p.IsDefault).Name);
        Assert.Equal("freeddns.org", parents.Default.Name);
        Assert.Equal(parents.Offered.OrderBy(p => p.Rank).Select(p => p.Name), parents.Offered.Select(p => p.Name));
    }

    [Fact]
    public void Every_offered_parent_is_on_the_pinned_Public_Suffix_List()
    {
        var parents = ParentDomains.Embedded();
        var list = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "public_suffix_list.excerpt.dat"));

        Assert.Empty(PublicSuffixList.MissingParents(parents, list, minRules: 10));
    }

    [Fact]
    public void Unsafe_parents_are_refused_with_a_reason_and_never_offered()
    {
        var parents = ParentDomains.Embedded();

        foreach (var name in Unsafe)
        {
            Assert.False(parents.Allows(name), name + " must not be offered");
            Assert.False(string.IsNullOrWhiteSpace(parents.BlockedReason(name)), name + " needs a reason");
        }

        Assert.DoesNotContain(parents.Offered, p => parents.Blocked.ContainsKey(p.Name));
    }

    [Fact]
    public void A_parent_that_is_not_in_the_catalogue_is_not_allowed_and_has_no_blocked_reason()
    {
        var parents = ParentDomains.Embedded();

        Assert.False(parents.Allows("example.com"));
        Assert.Null(parents.BlockedReason("example.com"));
        Assert.False(parents.Allows(null));
        Assert.False(parents.Allows(string.Empty));
    }

    [Fact]
    public void Allows_ignores_case_spaces_and_a_trailing_dot()
    {
        var parents = ParentDomains.Embedded();

        Assert.True(parents.Allows("  MyWire.ORG. "));
        Assert.NotNull(parents.BlockedReason(" DYNU.com "));
    }

    [Fact]
    public void Others_lists_every_offered_parent_except_the_one_given()
    {
        var parents = ParentDomains.Embedded();

        var others = parents.Others("MyWire.org");

        Assert.Equal(8, others.Count);
        Assert.DoesNotContain(others, p => p.Name == "mywire.org");
        Assert.Equal(others.OrderBy(p => p.Rank).Select(p => p.Name), others.Select(p => p.Name));
    }

    [Theory]
    [InlineData("""{"domains":[{"name":"a.org","rank":1},{"name":"b.org","rank":2}]}""")]
    [InlineData("""{"domains":[{"name":"a.org","rank":1,"default":true},{"name":"b.org","rank":2,"default":true}]}""")]
    [InlineData("""{"domains":[{"name":"a.org","rank":1,"default":true},{"name":"A.org","rank":2}]}""")]
    [InlineData("""{"domains":[{"name":"","rank":1,"default":true}]}""")]
    [InlineData("""{"domains":[{"name":"a.org","rank":1,"default":true}],"blocked":[{"name":"a.org","reason":"x"}]}""")]
    [InlineData("""{"domains":[{"name":"a.org","rank":1,"default":true}],"blocked":[{"name":"b.org","reason":""}]}""")]
    [InlineData("""{"domains":[]}""")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("not json")]
    public void A_bad_catalogue_is_refused(string json)
    {
        Assert.Throws<InvalidDataException>(() => ParentDomains.Parse(json));
    }

    [Fact]
    public void A_good_small_catalogue_loads()
    {
        var parents = ParentDomains.Parse("""{"domains":[{"name":"B.org","rank":2},{"name":"a.org","rank":1,"default":true}],"blocked":[{"name":"c.org","reason":"no"}]}""");

        Assert.Equal(new[] { "a.org", "b.org" }, parents.Offered.Select(p => p.Name));
        Assert.Equal("no", parents.BlockedReason("c.org"));
    }
}

using Jellyfin.Plugin.Trailhead.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class RenewalPolicyTests
{
    private static readonly DateTimeOffset D0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A 90-day certificate from D0, so the one-third point is day 60.</summary>
    internal sealed record Row(string Name, double? AriStart, double? AriEnd, double NowDay, double Random, double? CheckAtDay);

    internal static readonly IReadOnlyList<Row> Rows = new[]
    {
        new Row("no ARI, early: wait for the one-third point", null, null, 10, 0.5, 60),
        new Row("no ARI, exactly at the one-third point", null, null, 60, 0.5, null),
        new Row("no ARI, past the one-third point", null, null, 61, 0.5, null),
        new Row("ARI window earlier: renew inside it, at the random point", 30, 32, 10, 0.5, 31),
        new Row("ARI window earlier, random 0: the start of the window", 30, 32, 10, 0.0, 30),
        new Row("ARI window earlier, random near 1: near the end", 30, 32, 10, 0.75, 31.5),
        new Row("ARI window earlier, and we are at the random point", 30, 32, 31, 0.5, null),
        new Row("ARI window later than one third: the one-third rule still wins", 70, 72, 10, 0.5, 60),
        new Row("ARI window already over: renew now", 5, 6, 10, 0.5, null),
        new Row("expired certificate: renew now", null, null, 100, 0.5, null),
        new Row("expired certificate with a stale window: renew now", 70, 72, 100, 0.5, null),
    };

    public static IEnumerable<object[]> Table => Rows.Select((_, i) => new object[] { i });

    [Fact]
    public void The_table_is_not_empty()
    {
        // A table that enumerates nothing would make the theory below pass without running.
        Assert.True(Rows.Count >= 10);
        Assert.NotEmpty(Table);
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Decides_as_the_table_says(int index)
    {
        var row = Rows[index];
        var window = row.AriStart is { } s ? new RenewalWindow(D0.AddDays(s), D0.AddDays(row.AriEnd!.Value)) : null;

        var decision = RenewalPolicy.Decide(D0, D0.AddDays(90), window, D0.AddDays(row.NowDay), row.Random);

        if (row.CheckAtDay is { } day)
        {
            var at = Assert.IsType<RenewalDecision.CheckAt>(decision);
            Assert.Equal(D0.AddDays(day), at.Time);
        }
        else
        {
            Assert.IsType<RenewalDecision.RenewNow>(decision);
        }
    }

    [Fact]
    public void The_random_point_is_kept_across_calls()
    {
        var window = new RenewalWindow(D0.AddDays(30), D0.AddDays(32));
        var random = RenewalPolicy.FractionFor("AB12CD34");

        var first = Assert.IsType<RenewalDecision.CheckAt>(RenewalPolicy.Decide(D0, D0.AddDays(90), window, D0.AddDays(1), random));
        var later = Assert.IsType<RenewalDecision.CheckAt>(RenewalPolicy.Decide(D0, D0.AddDays(90), window, D0.AddDays(20), RenewalPolicy.FractionFor("AB12CD34")));

        Assert.Equal(first.Time, later.Time);
        Assert.InRange(first.Time, window.Start, window.End);
    }

    [Fact]
    public void Different_certificates_get_different_points_inside_zero_to_one()
    {
        var fractions = Enumerable.Range(0, 20).Select(i => RenewalPolicy.FractionFor("thumb" + i)).ToList();

        Assert.All(fractions, f => Assert.InRange(f, 0.0, 0.999999));
        Assert.True(fractions.Distinct().Count() > 10);
    }
}

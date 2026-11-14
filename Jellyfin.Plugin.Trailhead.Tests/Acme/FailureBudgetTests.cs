using System.Net.Http;
using Certes;
using Certes.Acme;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class FailureBudgetTests : IDisposable
{
    private const string Name = "home.example.org";
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _folder.Dispose();

    private FailureBudget Budget() => new(_folder.File("budget.json"), _time);

    [Fact]
    public void Two_failures_are_allowed_and_the_third_blocks_until_the_first_ages_out()
    {
        var budget = Budget();
        var first = _time.GetUtcNow();

        budget.RecordFailure(Name);
        _time.Advance(TimeSpan.FromMinutes(5));
        budget.RecordFailure(Name);
        Assert.Null(budget.BlockedUntil(Name));

        _time.Advance(TimeSpan.FromMinutes(5));
        budget.RecordFailure(Name);

        Assert.Equal(first.AddHours(1), budget.BlockedUntil(Name));
    }

    [Fact]
    public void The_limit_is_per_name_and_ignores_case()
    {
        var budget = Budget();

        for (var i = 0; i < 3; i++)
        {
            budget.RecordFailure("Home.Example.Org");
        }

        Assert.NotNull(budget.BlockedUntil("home.example.org"));
        Assert.Null(budget.BlockedUntil("other.example.org"));
    }

    [Fact]
    public void The_hour_rolls_over()
    {
        var budget = Budget();
        budget.RecordFailure(Name);
        _time.Advance(TimeSpan.FromMinutes(20));
        budget.RecordFailure(Name);
        _time.Advance(TimeSpan.FromMinutes(20));
        budget.RecordFailure(Name);
        Assert.NotNull(budget.BlockedUntil(Name));

        // One minute past the first failure's hour: two failures are left in the window.
        _time.Advance(TimeSpan.FromMinutes(21));

        Assert.Null(budget.BlockedUntil(Name));
    }

    [Fact]
    public void A_block_is_remembered_by_a_new_instance()
    {
        var first = Budget();
        for (var i = 0; i < 3; i++)
        {
            first.RecordFailure(Name);
        }

        var blocked = Budget().BlockedUntil(Name);

        Assert.NotNull(blocked);
        Assert.True(blocked > _time.GetUtcNow());
    }

    [Fact]
    public void An_unreadable_file_means_no_failures_rather_than_an_error()
    {
        File.WriteAllText(_folder.File("budget.json"), "{ not json");

        Assert.Null(Budget().BlockedUntil(Name));
    }

    [Fact]
    public void A_missing_file_means_no_failures()
    {
        Assert.Null(Budget().BlockedUntil(Name));
    }

    internal sealed record Case(string Name, Exception Error, FailureKind Expected);

    private static Exception Acme(string type, int status) => new AcmeRequestException("x", new AcmeError { Type = "urn:ietf:params:acme:error:" + type, Status = (System.Net.HttpStatusCode)status });

    internal static readonly IReadOnlyList<Case> Cases = new[]
    {
        new Case("timeout", new TimeoutException(), FailureKind.Transient),
        new Case("network", new HttpRequestException("down"), FailureKind.Transient),
        new Case("io", new IOException("reset"), FailureKind.Transient),
        new Case("5xx", Acme("serverInternal", 500), FailureKind.Transient),
        new Case("503 with no type", Acme("other", 503), FailureKind.Transient),
        new Case("rateLimited", Acme("rateLimited", 429), FailureKind.Transient),
        new Case("badNonce", Acme("badNonce", 400), FailureKind.Transient),
        new Case("unauthorized", Acme("unauthorized", 403), FailureKind.Permanent),
        new Case("badCSR", Acme("badCSR", 400), FailureKind.Permanent),
        new Case("invalidProfile", Acme("invalidProfile", 400), FailureKind.Permanent),
        new Case("rejectedIdentifier", Acme("rejectedIdentifier", 400), FailureKind.Permanent),
        new Case("malformed", Acme("malformed", 400), FailureKind.Permanent),
        new Case("alreadyReplaced", Acme("alreadyReplaced", 409), FailureKind.Permanent),
        new Case("invalid authorization: unauthorized", new AuthorizationInvalidException(Name, "urn:ietf:params:acme:error:unauthorized", "wrong TXT"), FailureKind.Permanent),
        new Case("invalid authorization: dns", new AuthorizationInvalidException(Name, "urn:ietf:params:acme:error:dns", "no answer"), FailureKind.Transient),
        new Case("invalid authorization: no detail", new AuthorizationInvalidException(Name, null, null), FailureKind.Permanent),
        new Case("anything unknown retries later", new InvalidOperationException("?"), FailureKind.Transient),
    };

    public static IEnumerable<object[]> CaseTable => Cases.Select((_, i) => new object[] { i });

    [Fact]
    public void The_classification_table_is_not_empty()
    {
        Assert.True(Cases.Count >= 15);
    }

    [Theory]
    [MemberData(nameof(CaseTable))]
    public void Classifies_failures(int index)
    {
        var c = Cases[index];
        Assert.True(c.Expected == FailureBudget.Classify(c.Error), c.Name);
    }
}

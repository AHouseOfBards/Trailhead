using System.Net;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class DynuChallengeTests : IAsyncLifetime
{
    private static readonly IPEndPoint Server = new(IPAddress.Parse("192.0.2.53"), 53);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger _log = new();
    private readonly FakeLookups _lookups = new();
    private FakeDynu _dynu = null!;
    private DynuClient _client = null!;
    private DynuChallenge _challenge = null!;

    public async Task InitializeAsync()
    {
        _dynu = await FakeDynu.StartAsync();
        _client = DynuClient.Create(FakeDynu.Key, _log, _time, _dynu.BaseUrl, new DynuClientOptions { RequestTimeout = TimeSpan.FromHours(1) });
        var waiter = new AuthoritativeDnsWaiter(_lookups, _time, _log, fixedServers: new[] { Server });
        _challenge = new DynuChallenge(_client, waiter, _log);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _dynu.DisposeAsync();
    }

    [Fact]
    public async Task The_name_goes_to_its_domain_with_the_challenge_label_as_the_node()
    {
        var other = _dynu.AddDomain("other.example.net");
        var home = _dynu.AddDomain("home.example.org");

        var handle = await _challenge.SetTxtAsync("_acme-challenge.home.example.org", "the-value", CancellationToken.None);

        var record = Assert.Single(_dynu.Records);
        Assert.Equal((home, "_acme-challenge", "the-value"), (record.DomainId, record.NodeName, record.Text));
        Assert.NotEqual(other, record.DomainId);
        Assert.Equal($"{home}:{record.Id}", handle);
    }

    [Fact]
    public async Task The_longest_matching_domain_wins_and_the_node_carries_the_rest_of_the_name()
    {
        var parent = _dynu.AddDomain("example.org");
        var child = _dynu.AddDomain("Home.Example.ORG");

        await _challenge.SetTxtAsync("_acme-challenge.home.example.org", "a", CancellationToken.None);
        await _challenge.SetTxtAsync("_acme-challenge.other.example.org", "b", CancellationToken.None);

        var records = _dynu.Records;
        Assert.Equal((child, "_acme-challenge"), (records[0].DomainId, records[0].NodeName));
        Assert.Equal((parent, "_acme-challenge.other"), (records[1].DomainId, records[1].NodeName));
    }

    [Fact]
    public async Task A_trailing_dot_and_capitals_in_the_name_do_not_matter()
    {
        var home = _dynu.AddDomain("home.example.org");

        await _challenge.SetTxtAsync("_acme-challenge.HOME.example.org.", "v", CancellationToken.None);

        Assert.Equal(home, Assert.Single(_dynu.Records).DomainId);
    }

    [Fact]
    public async Task A_name_the_account_does_not_have_is_a_permanent_error()
    {
        // "xample.org" is a suffix of the name as text but not a parent zone of it.
        _dynu.AddDomain("xample.org");
        _dynu.AddDomain("other.example.net");

        var error = await Assert.ThrowsAsync<DynuException>(() => _challenge.SetTxtAsync("_acme-challenge.home.example.org", "v", CancellationToken.None));

        Assert.Equal(FailureKind.Permanent, FailureBudget.Classify(error));
        Assert.Empty(_dynu.Records);
        Assert.DoesNotContain(_dynu.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task An_account_with_no_domains_fails_instead_of_passing()
    {
        var error = await Assert.ThrowsAsync<DynuException>(() => _challenge.SetTxtAsync("_acme-challenge.home.example.org", "v", CancellationToken.None));

        Assert.Equal(FailureKind.Permanent, error.Kind);
    }

    [Fact]
    public async Task The_handle_round_trips_and_removing_twice_is_fine()
    {
        _dynu.AddDomain("home.example.org");
        var handle = await _challenge.SetTxtAsync("_acme-challenge.home.example.org", "v", CancellationToken.None);

        await _challenge.RemoveAsync(handle, CancellationToken.None);
        Assert.Empty(_dynu.Records);

        await _challenge.RemoveAsync(handle, CancellationToken.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("1:2:3")]
    [InlineData("1:")]
    [InlineData(":2")]
    [InlineData("-1:5")]
    [InlineData("0:0")]
    public async Task A_handle_that_is_not_ours_is_refused_and_nothing_is_sent(string handle)
    {
        await Assert.ThrowsAsync<DynuException>(() => _challenge.RemoveAsync(handle, CancellationToken.None));

        Assert.Empty(_dynu.Requests);
    }

    [Fact]
    public async Task Waiting_hands_the_name_and_value_to_the_waiter()
    {
        _lookups.Txt[Server] = new[] { "other", "the-value" };

        await Clock.WithinAsync(_challenge.WaitUntilVisibleAsync("_acme-challenge.home.example.org", "the-value", CancellationToken.None));

        Assert.Equal(("_acme-challenge.home.example.org", Server), (_lookups.TxtAsked[0].Name, _lookups.TxtAsked[0].Server));
    }
}

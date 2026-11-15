using System.Net;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class AuthoritativeDnsWaiterTests
{
    private const string Host = "_acme-challenge.home.example.org";
    private const string Value = "the-value";

    private static readonly IPEndPoint S1 = new(IPAddress.Parse("192.0.2.1"), 53);
    private static readonly IPEndPoint S2 = new(IPAddress.Parse("192.0.2.2"), 53);
    private static readonly IPEndPoint S3 = new(IPAddress.Parse("192.0.2.3"), 53);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger _log = new();
    private readonly FakeLookups _lookups = new();

    private AuthoritativeDnsWaiter Waiter(IReadOnlyList<IPEndPoint>? fixedServers = null)
        => new(_lookups, _time, _log, fixedServers: fixedServers);

    [Fact]
    public async Task It_returns_once_every_server_serves_the_value()
    {
        foreach (var s in new[] { S1, S2, S3 })
        {
            _lookups.Txt[s] = new[] { "unrelated", Value };
        }

        await Clock.WithinAsync(Waiter(new[] { S1, S2, S3 }).WaitAsync(Host, Value, CancellationToken.None));

        Assert.Equal(3, _lookups.TxtAsked.Count);
    }

    [Fact]
    public async Task One_server_lagging_keeps_it_waiting_until_that_server_has_the_value()
    {
        _lookups.Txt[S1] = new[] { Value };
        _lookups.Txt[S2] = new[] { Value };
        _lookups.Txt[S3] = Array.Empty<string>();
        var task = Waiter(new[] { S1, S2, S3 }).WaitAsync(Host, Value, CancellationToken.None);

        // Well past several rounds, and still waiting: two of three is not enough.
        for (var i = 0; i < 6; i++)
        {
            await Clock.UntilAsync(() => _lookups.TxtAsked.Count >= 3 * (i + 1), "a round");
            await Task.Delay(50);
            _time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.False(task.IsCompleted);

        _lookups.Txt[S3] = new[] { Value };
        await Clock.PumpAsync(task, _time);
    }

    [Fact]
    public async Task A_server_with_the_wrong_value_does_not_count()
    {
        _lookups.Txt[S1] = new[] { Value };
        _lookups.Txt[S2] = new[] { "not-" + Value };

        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Clock.PumpAsync(Waiter(new[] { S1, S2 }).WaitAsync(Host, Value, CancellationToken.None), _time));

        Assert.Contains("1 of 2", error.Message);
    }

    [Fact]
    public async Task A_server_that_cannot_be_asked_counts_as_not_serving_it()
    {
        _lookups.Txt[S1] = new[] { Value };

        // S2 has no entry, so asking it throws.
        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Clock.PumpAsync(Waiter(new[] { S1, S2 }).WaitAsync(Host, Value, CancellationToken.None), _time));

        Assert.Contains("1 of 2", error.Message);
    }

    [Fact]
    public async Task It_gives_up_after_ten_minutes_and_says_how_many_servers_had_it()
    {
        _lookups.Txt[S1] = new[] { Value };
        _lookups.Txt[S2] = Array.Empty<string>();
        var started = _time.GetUtcNow();

        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Clock.PumpAsync(Waiter(new[] { S1, S2 }).WaitAsync(Host, Value, CancellationToken.None), _time, TimeSpan.FromSeconds(1)));

        Assert.Contains("1 of 2", error.Message);
        Assert.InRange(_time.GetUtcNow() - started, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task It_polls_every_five_seconds()
    {
        _lookups.Txt[S1] = Array.Empty<string>();
        var task = Waiter(new[] { S1 }).WaitAsync(Host, Value, CancellationToken.None);
        await Clock.UntilAsync(() => _lookups.TxtAsked.Count == 1, "the first round");
        await Task.Delay(50);

        _time.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(100);
        Assert.Single(_lookups.TxtAsked);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        await Clock.UntilAsync(() => _lookups.TxtAsked.Count == 2, "the second round");

        _lookups.Txt[S1] = new[] { Value };
        await Clock.PumpAsync(task, _time);
    }

    [Fact]
    public async Task An_empty_server_list_is_a_failure_and_asks_nothing()
    {
        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Waiter(Array.Empty<IPEndPoint>()).WaitAsync(Host, Value, CancellationToken.None));

        Assert.Contains("No authoritative nameservers", error.Message);
        Assert.Empty(_lookups.NsAsked);
        Assert.Empty(_lookups.TxtAsked);
    }

    [Fact]
    public async Task No_nameservers_anywhere_up_the_name_is_a_failure_and_the_TXT_is_never_asked_of_anyone_else()
    {
        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Waiter().WaitAsync(Host, Value, CancellationToken.None));

        Assert.Contains("No authoritative nameservers", error.Message);
        Assert.Empty(_lookups.TxtAsked);

        // Every zone from the name up to the last one with two labels was tried.
        Assert.Equal(new[] { Host, "home.example.org", "example.org" }, _lookups.NsAsked);
    }

    [Fact]
    public async Task It_finds_the_nameservers_at_the_hostname_itself_as_Dynu_hostnames_have_them()
    {
        _lookups.Ns["home.example.org"] = new[] { "ns1.dynu.example", "ns2.dynu.example" };
        _lookups.V4["ns1.dynu.example"] = new[] { S1.Address };
        _lookups.V4["ns2.dynu.example"] = new[] { S2.Address };
        _lookups.Txt[S1] = new[] { Value };
        _lookups.Txt[S2] = new[] { Value };

        await Clock.WithinAsync(Waiter().WaitAsync(Host, Value, CancellationToken.None));

        Assert.Equal(new[] { Host, "home.example.org" }, _lookups.NsAsked);
        Assert.Equal(new[] { S1, S2 }, _lookups.TxtAsked.Select(a => a.Server).OrderBy(s => s.Address.ToString()));
        Assert.All(_lookups.TxtAsked, a => Assert.Equal(Host, a.Name));
    }

    [Fact]
    public async Task It_finds_them_on_a_parent_zone_when_the_name_is_a_plain_subdomain()
    {
        _lookups.Ns["example.org"] = new[] { "ns1.example.org" };
        _lookups.V4["ns1.example.org"] = new[] { S1.Address };
        _lookups.Txt[S1] = new[] { Value };

        await Clock.WithinAsync(Waiter().WaitAsync(Host, Value, CancellationToken.None));

        Assert.Equal(new[] { Host, "home.example.org", "example.org" }, _lookups.NsAsked);
    }

    [Fact]
    public async Task A_nameserver_is_asked_on_IPv4_and_only_on_IPv6_when_it_has_no_IPv4()
    {
        var v6 = IPAddress.Parse("2001:db8::53");
        _lookups.Ns["home.example.org"] = new[] { "both.example", "six.example" };
        _lookups.V4["both.example"] = new[] { S1.Address };
        _lookups.V6["both.example"] = new[] { IPAddress.Parse("2001:db8::1") };
        _lookups.V6["six.example"] = new[] { v6 };
        _lookups.Txt[S1] = new[] { Value };
        _lookups.Txt[new IPEndPoint(v6, 53)] = new[] { Value };

        await Clock.WithinAsync(Waiter().WaitAsync(Host, Value, CancellationToken.None));

        Assert.Equal(2, _lookups.TxtAsked.Count);
        Assert.Contains(_lookups.TxtAsked, a => a.Server.Equals(S1));
        Assert.Contains(_lookups.TxtAsked, a => a.Server.Equals(new IPEndPoint(v6, 53)));
    }

    [Fact]
    public async Task A_zone_whose_lookup_fails_is_skipped_and_the_next_one_up_is_tried()
    {
        _lookups.NsThrows.Add("home.example.org");
        _lookups.Ns["example.org"] = new[] { "ns1.example.org" };
        _lookups.V4["ns1.example.org"] = new[] { S1.Address };
        _lookups.Txt[S1] = new[] { Value };

        await Clock.WithinAsync(Waiter().WaitAsync(Host, Value, CancellationToken.None));

        Assert.Single(_lookups.TxtAsked);
    }

    [Fact]
    public async Task A_resolver_that_is_down_for_every_zone_is_a_failure_not_a_throw_of_its_own_error()
    {
        _lookups.NsThrows.Add(Host);
        _lookups.NsThrows.Add("home.example.org");
        _lookups.NsThrows.Add("example.org");

        await Assert.ThrowsAsync<DnsWaitException>(() => Waiter().WaitAsync(Host, Value, CancellationToken.None));
    }

    [Fact]
    public async Task A_nameserver_with_no_addresses_is_not_a_zone_with_nameservers()
    {
        _lookups.Ns["home.example.org"] = new[] { "ghost.example" };

        await Assert.ThrowsAsync<DnsWaitException>(() => Waiter().WaitAsync(Host, Value, CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_stops_the_wait()
    {
        _lookups.Txt[S1] = Array.Empty<string>();
        using var cts = new CancellationTokenSource();
        var task = Waiter(new[] { S1 }).WaitAsync(Host, Value, cts.Token);
        await Clock.UntilAsync(() => _lookups.TxtAsked.Count == 1, "the first round");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_trailing_dot_on_the_name_is_ignored()
    {
        _lookups.Txt[S1] = new[] { Value };

        await Clock.WithinAsync(Waiter(new[] { S1 }).WaitAsync(Host + ".", Value, CancellationToken.None));

        Assert.Equal(Host, _lookups.TxtAsked[0].Name);
    }
}

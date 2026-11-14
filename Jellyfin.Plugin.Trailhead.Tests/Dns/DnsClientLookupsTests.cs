using System.Net;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

/// <summary>The waiter on the real DnsClient code, against a DNS server of our own on a UDP socket.</summary>
public sealed class DnsClientLookupsTests : IDisposable
{
    private const string Host = "_acme-challenge.home.example.org";
    private const string Value = "the-value";

    private readonly FakeDnsServer _dns = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger _log = new();

    public void Dispose() => _dns.Dispose();

    private AuthoritativeDnsWaiter Waiter(IReadOnlyList<IPEndPoint>? fixedServers = null, bool discover = false)
        => AuthoritativeDnsWaiter.Create(
            _log,
            _time,
            new DnsWaitOptions
            {
                QueryTimeout = TimeSpan.FromSeconds(2),
                NameServerPort = _dns.Endpoint.Port,
                DiscoveryServers = discover ? new[] { _dns.Endpoint } : null,
            },
            fixedServers);

    [Fact]
    public async Task It_asks_the_server_itself_with_recursion_off_and_sees_the_value()
    {
        _dns.Txt[Host] = new[] { "unrelated", Value };

        await Clock.WithinAsync(Waiter(new[] { _dns.Endpoint }).WaitAsync(Host, Value, CancellationToken.None), 15);

        var query = Assert.Single(_dns.Queries, q => q.Type == 16);
        Assert.Equal(Host, query.Name);
        Assert.False(query.RecursionDesired);
    }

    [Fact]
    public async Task It_finds_the_nameservers_then_asks_each_of_them_and_never_with_recursion()
    {
        _dns.Ns["home.example.org"] = new[] { "ns1.test.example" };
        _dns.A["ns1.test.example"] = IPAddress.Loopback;
        _dns.Txt[Host] = new[] { Value };

        // Discovery and the TXT question both land on the fake, which is the zone's one nameserver here.
        await Clock.WithinAsync(Waiter(discover: true).WaitAsync(Host, Value, CancellationToken.None), 15);

        Assert.Contains(_dns.Queries, q => q is { Type: 2, Name: "home.example.org" });
        Assert.Contains(_dns.Queries, q => q is { Type: 1, Name: "ns1.test.example" });
        Assert.All(_dns.Queries.Where(q => q.Type == 16), q => Assert.False(q.RecursionDesired));
    }

    [Fact]
    public async Task A_value_that_is_not_there_yet_is_not_visible()
    {
        _dns.Txt[Host] = new[] { "other" };

        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Clock.PumpAsync(Waiter(new[] { _dns.Endpoint }).WaitAsync(Host, Value, CancellationToken.None), _time));

        Assert.Contains("0 of 1", error.Message);
    }

    [Fact]
    public async Task A_zone_with_no_NS_records_anywhere_fails_instead_of_asking_the_system_resolver()
    {
        var error = await Assert.ThrowsAsync<DnsWaitException>(() => Waiter(discover: true).WaitAsync(Host, Value, CancellationToken.None));

        Assert.Contains("No authoritative nameservers", error.Message);
        Assert.DoesNotContain(_dns.Queries, q => q.Type == 16);
    }
}

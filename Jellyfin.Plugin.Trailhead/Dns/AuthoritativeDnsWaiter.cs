using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Dns;

/// <summary>What the waiter needs from DNS. DnsClient behind it in the plugin; a fake in the decision tests.</summary>
internal interface IDnsLookups
{
    /// <summary>The names of the zone's nameservers, or none when the name is not a zone apex. Asked of the system's resolver.</summary>
    Task<IReadOnlyList<string>> LookupNsAsync(string zone, CancellationToken ct);

    /// <summary>One family's addresses for a nameserver's name. Asked of the system's resolver.</summary>
    Task<IReadOnlyList<IPAddress>> LookupAddressesAsync(string host, AddressFamily family, CancellationToken ct);

    /// <summary>The TXT values one server serves for the name, asked of that server alone with recursion off. Throws if it can't be asked.</summary>
    Task<IReadOnlyList<string>> QueryTxtAsync(IPEndPoint server, string name, CancellationToken ct);
}

internal sealed class DnsWaitOptions
{
    internal TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The longest one nameserver gets to answer one question.</summary>
    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Port of the zone's nameservers. 53; tests run a DNS server on another.</summary>
    internal int NameServerPort { get; init; } = 53;

    /// <summary>Where nameservers are looked up. Null means the system's resolver; tests point it at their own.</summary>
    internal IReadOnlyList<IPEndPoint>? DiscoveryServers { get; init; }
}

/// <summary>
/// Waits until every authoritative nameserver of the name's zone serves the TXT value. Let's Encrypt doesn't say
/// which one it will ask, so a record that is on most of them makes validation flaky, and a failed validation counts
/// against the 5 per hostname per hour. It never asks the system resolver for the TXT: that caches, negative
/// answers included for the zone's negative TTL (1800 s in RASA's runs), so a lookup a second too early could report
/// "not there" for half an hour.
/// </summary>
internal sealed class AuthoritativeDnsWaiter
{
    private readonly IDnsLookups _lookups;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly DnsWaitOptions _options;
    private readonly IReadOnlyList<IPEndPoint>? _fixedServers;

    /// <param name="fixedServers">Tests' servers, used instead of finding them. An empty list is a failure, not a request to find them.</param>
    internal AuthoritativeDnsWaiter(IDnsLookups lookups, TimeProvider time, ILogger logger, DnsWaitOptions? options = null, IReadOnlyList<IPEndPoint>? fixedServers = null)
    {
        _lookups = lookups;
        _time = time;
        _logger = logger;
        _options = options ?? new DnsWaitOptions();
        _fixedServers = fixedServers;
    }

    internal static AuthoritativeDnsWaiter Create(ILogger logger, TimeProvider time, DnsWaitOptions? options = null, IReadOnlyList<IPEndPoint>? fixedServers = null)
    {
        var o = options ?? new DnsWaitOptions();
        return new AuthoritativeDnsWaiter(new DnsClientLookups(o.QueryTimeout, o.DiscoveryServers), time, logger, o, fixedServers);
    }

    internal async Task WaitAsync(string name, string value, CancellationToken ct)
    {
        var host = name.Trim().TrimEnd('.');
        var servers = _fixedServers ?? await FindServersAsync(host, ct).ConfigureAwait(false);
        if (servers.Count == 0)
        {
            // Nothing trustworthy to ask. Falling back to the system resolver would bring the caching back.
            throw new DnsWaitException("No authoritative nameservers were found for " + host + ".");
        }

        var started = _time.GetTimestamp();
        for (var round = 1; ; round++)
        {
            var answers = await Task.WhenAll(servers.Select(s => ServesAsync(s, host, value, ct))).ConfigureAwait(false);
            var seen = answers.Count(a => a);
            _logger.LogDebug("DNS challenge round {Round}: {Seen} of {Total} nameservers serve the record.", round, seen, servers.Count);
            if (seen == servers.Count)
            {
                _logger.LogInformation("The DNS challenge record is on all {Total} nameservers for {Name} (round {Round}).", servers.Count, host, round);
                return;
            }

            var elapsed = _time.GetElapsedTime(started);
            if (elapsed >= _options.Timeout)
            {
                throw new DnsWaitException($"The DNS challenge record for {host} was on {seen} of {servers.Count} nameservers after {elapsed.TotalSeconds:0} s.");
            }

            var left = _options.Timeout - elapsed;
            await Task.Delay(left < _options.Interval ? left : _options.Interval, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ServesAsync(IPEndPoint server, string name, string value, CancellationToken ct)
    {
        // Our own bound, through TimeProvider, besides the one inside DnsClient.
        using var limit = new CancellationTokenSource(_options.QueryTimeout + TimeSpan.FromSeconds(1), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            var values = await _lookups.QueryTxtAsync(server, name, linked.Token).ConfigureAwait(false);
            return values.Any(v => v.Trim() == value);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // A server that can't be asked counts as not serving it yet; the next round asks again.
            _logger.LogDebug("A nameserver could not be asked for the DNS challenge record ({Error}).", e.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Walks up from the name to the nearest zone that has NS records (for a Dynu hostname that is the hostname itself,
    /// for a subdomain a parent), then takes each nameserver's IPv4 addresses, or its IPv6 ones when it has no IPv4.
    /// </summary>
    private async Task<IReadOnlyList<IPEndPoint>> FindServersAsync(string host, CancellationToken ct)
    {
        var labels = host.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var zone = string.Join('.', labels[i..]);
            var found = new List<IPEndPoint>();
            try
            {
                foreach (var ns in await _lookups.LookupNsAsync(zone, ct).ConfigureAwait(false))
                {
                    var v4 = await _lookups.LookupAddressesAsync(ns, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                    var addresses = v4.Count > 0 ? v4 : await _lookups.LookupAddressesAsync(ns, AddressFamily.InterNetworkV6, ct).ConfigureAwait(false);
                    found.AddRange(addresses.Select(a => new IPEndPoint(a, _options.NameServerPort)));
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("Looking up the nameservers of {Zone} failed ({Error}).", zone, e.GetType().Name);
                continue;
            }

            if (found.Count > 0)
            {
                return found.Distinct().ToList();
            }
        }

        return Array.Empty<IPEndPoint>();
    }
}

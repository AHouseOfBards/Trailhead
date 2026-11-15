using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;

namespace Jellyfin.Plugin.Trailhead.Dns;

/// <summary>The waiter's lookups through DnsClient (T44).</summary>
internal sealed class DnsClientLookups : IDnsLookups
{
    private readonly TimeSpan _timeout;
    private readonly LookupClient _system;
    private readonly ConcurrentDictionary<IPEndPoint, LookupClient> _perServer = new();

    internal DnsClientLookups(TimeSpan timeout, IReadOnlyList<IPEndPoint>? discoveryServers = null)
    {
        _timeout = timeout;

        // The system's nameservers (AutoResolveNameServers is on when none are given). They only find who the zone's
        // nameservers are; nothing the answer depends on is read from them.
        var options = discoveryServers is { Count: > 0 } ? new LookupClientOptions(discoveryServers.Select(s => new NameServer(s)).ToArray()) : new LookupClientOptions();
        options.UseCache = false;
        options.Timeout = timeout;
        options.Retries = 1;
        _system = new LookupClient(options);
    }

    public async Task<IReadOnlyList<string>> LookupNsAsync(string zone, CancellationToken ct)
    {
        var response = await _system.QueryAsync(zone, QueryType.NS, cancellationToken: ct).ConfigureAwait(false);
        return response.Answers.NsRecords().Select(r => r.NSDName.Value.TrimEnd('.')).ToList();
    }

    public async Task<IReadOnlyList<IPAddress>> LookupAddressesAsync(string host, AddressFamily family, CancellationToken ct)
    {
        var type = family == AddressFamily.InterNetworkV6 ? QueryType.AAAA : QueryType.A;
        var response = await _system.QueryAsync(host, type, cancellationToken: ct).ConfigureAwait(false);
        return response.Answers.OfType<AddressRecord>().Select(r => r.Address).ToList();
    }

    public async Task<IReadOnlyList<string>> QueryTxtAsync(IPEndPoint server, string name, CancellationToken ct)
    {
        var client = _perServer.GetOrAdd(server, s => new LookupClient(new LookupClientOptions(new NameServer(s))
        {
            // Recursion off and no cache: the answer has to come from that server's own zone, now. TCP fallback for
            // an answer that doesn't fit one UDP packet.
            Recursion = false,
            UseCache = false,
            UseTcpFallback = true,
            Timeout = _timeout,
            Retries = 0,
            ThrowDnsErrors = false,
        }));

        var response = await client.QueryAsync(name, QueryType.TXT, cancellationToken: ct).ConfigureAwait(false);
        return response.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
    }
}

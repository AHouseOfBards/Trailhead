using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Jellyfin.Plugin.Trailhead.Network;

/// <summary>The addresses the outside world sees for this server. Each may be null: one family or both can be missing.</summary>
internal sealed record PublicAddresses(IPAddress? V4, IPAddress? V6);

internal interface IPublicAddressLookup
{
    Task<PublicAddresses> LookupAsync(CancellationToken ct);
}

/// <summary>
/// Asks plain-text "what's my IP" services, IPv4 and IPv6 separately (A5, from RASA's <c>probe/internet.go</c>). Each
/// request is pinned to one address family in <see cref="SocketsHttpHandler.ConnectCallback"/>: on a dual-stack host
/// an unpinned request may go out over IPv6 and come back with the IPv6 address for the IPv4 question, and the
/// CGNAT comparison (T32) then compares the router's WAN with the wrong thing. Several services per family, tried in
/// order, because any one can be down or blocked by a DNS filter, and that must not look like "no internet".
/// </summary>
internal sealed class PublicAddressLookup : IPublicAddressLookup, IDisposable
{
    internal static readonly IReadOnlyList<Uri> DefaultV4 = new[]
    {
        new Uri("https://api.ipify.org/"),
        new Uri("https://ipv4.icanhazip.com/"),
        new Uri("https://v4.ident.me/"),
    };

    internal static readonly IReadOnlyList<Uri> DefaultV6 = new[]
    {
        new Uri("https://api6.ipify.org/"),
        new Uri("https://ipv6.icanhazip.com/"),
        new Uri("https://v6.ident.me/"),
    };

    private static readonly TimeSpan EachTimeout = TimeSpan.FromSeconds(5);
    private const int MaxBody = 128;

    private readonly HttpClient _v4;
    private readonly HttpClient _v6;
    private readonly IReadOnlyList<Uri> _v4Endpoints;
    private readonly IReadOnlyList<Uri> _v6Endpoints;

    internal PublicAddressLookup(IReadOnlyList<Uri>? v4 = null, IReadOnlyList<Uri>? v6 = null)
    {
        _v4 = Pinned(AddressFamily.InterNetwork);
        _v6 = Pinned(AddressFamily.InterNetworkV6);
        _v4Endpoints = v4 ?? DefaultV4;
        _v6Endpoints = v6 ?? DefaultV6;
    }

    /// <summary>A client whose every connection uses <paramref name="family"/> only, whatever DNS returns.</summary>
    internal static HttpClient Pinned(AddressFamily family)
    {
        var handler = new SocketsHttpHandler
        {
            // Not reused: an address change between two probes must be seen.
            PooledConnectionLifetime = TimeSpan.Zero,
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = EachTimeout,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, ct).ConfigureAwait(false);
                if (addresses.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return new HttpClient(handler) { Timeout = EachTimeout };
    }

    public async Task<PublicAddresses> LookupAsync(CancellationToken ct)
    {
        var v4 = FirstAsync(_v4, _v4Endpoints, AddressFamily.InterNetwork, ct);
        var v6 = FirstAsync(_v6, _v6Endpoints, AddressFamily.InterNetworkV6, ct);
        return new PublicAddresses(await v4.ConfigureAwait(false), await v6.ConfigureAwait(false));
    }

    private static async Task<IPAddress?> FirstAsync(HttpClient client, IReadOnlyList<Uri> endpoints, AddressFamily family, CancellationToken ct)
    {
        foreach (var endpoint in endpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await AskAsync(client, endpoint, family, ct).ConfigureAwait(false) is { } found)
                {
                    return found;
                }
            }
            catch (Exception e) when (e is HttpRequestException or SocketException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Down, blocked, or no route for this family. The next one may answer.
            }
        }

        return null;
    }

    private static async Task<IPAddress?> AskAsync(HttpClient client, Uri endpoint, AddressFamily family, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("text/plain");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBody + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        // A web page instead of an address (a captive portal, a filter) is longer than any address.
        return total > MaxBody ? null : Parse(Encoding.ASCII.GetString(buffer, 0, total), family);
    }

    /// <summary>An address of the asked family that could be someone's public address, or null.</summary>
    internal static IPAddress? Parse(string text, AddressFamily family)
    {
        if (!IPAddress.TryParse(text.Trim(), out var address) || address.AddressFamily != family || address.IsIPv4MappedToIPv6)
        {
            return null;
        }

        return IsPublic(address) ? address : null;
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && (b[1] & 0xC0) == 64));
        }

        // Global unicast is 2000::/3. ULA (fc00::/7), link-local and multicast fall outside it.
        return (b[0] & 0xE0) == 0x20;
    }

    public void Dispose()
    {
        _v4.Dispose();
        _v6.Dispose();
    }
}

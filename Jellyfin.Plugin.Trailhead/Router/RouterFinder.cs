using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.Trailhead.Network;

namespace Jellyfin.Plugin.Trailhead.Router;

internal enum RouterStatus
{
    /// <summary>A router answered and described a port-mapping service.</summary>
    Found,

    /// <summary>Nothing answered SSDP. UPnP is off, or the search never reached the router (a VPN, a Docker bridge).</summary>
    NoReply,

    /// <summary>Something answered but wouldn't describe itself.</summary>
    NoDescription,

    /// <summary>A router answered but has no port-mapping service: usually the "UPnP" switch that is media sharing.</summary>
    NoPortService,
}

/// <summary>What a search found. <see cref="Manufacturer"/>/<see cref="Model"/> may be set without a device (NoPortService).</summary>
internal sealed record RouterSearchResult(RouterStatus Status, IgdDevice? Device, string Manufacturer = "", string Model = "");

/// <summary>Finds the router's port-mapping service. A seam: the mapper and the probe take one.</summary>
internal interface IRouterFinder
{
    Task<RouterSearchResult> FindAsync(CancellationToken ct);
}

/// <summary>One socket's worth of a search: where to bind, and where to send.</summary>
internal sealed record SearchPlan(IPAddress? Bind, IReadOnlyList<IPEndPoint> Targets);

/// <summary>
/// SSDP discovery and the device description, ported from RASA's <c>probe/router.go</c>. Its live-learned facts kept:
/// <list type="bullet">
/// <item>A v2-only router stays silent to a v1 search, which looks exactly like "UPnP is off" (a real report), so the
/// search asks for IGD v1 and v2, the WAN services, and <c>upnp:rootdevice</c> (A5).</item>
/// <item>The search goes out from the router-facing address and is also sent unicast to each gateway, because with a
/// VPN up the multicast leaves by the tunnel and the router never hears it (a real report).</item>
/// <item>Every datagram is sent twice: SSDP is UDP and busy Wi-Fi loses one often enough.</item>
/// <item><c>upnp:rootdevice</c> is answered by televisions and printers too, so every answer is described and only one
/// with a WAN service counts; ones that say "InternetGatewayDevice" are tried first.</item>
/// </list>
/// Not kept: RASA trusted any LOCATION it was handed. Here the description must come from the address that answered,
/// and the control URL must be on that same host, so another device on the LAN can't steer Trailhead's SOAP calls
/// somewhere else.
/// </summary>
internal sealed class RouterFinder : IRouterFinder
{
    internal static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);

    internal static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "upnp:rootdevice",
    };

    // Best first. A router with both is asked for its address on each in turn, so a dead PPP service is skipped.
    private static readonly string[] ServicePreference =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    };

    private const int MaxCandidates = 8;
    private const int MaxDatagram = 2048;

    private readonly HttpClient _http;
    private readonly Func<IReadOnlyList<SearchPlan>> _plans;
    private readonly TimeSpan _window;

    internal RouterFinder(HttpClient http, Func<IReadOnlyList<SearchPlan>> plans, TimeSpan window)
    {
        _http = http;
        _plans = plans;
        _window = window;
    }

    /// <summary>Plans for this machine: one socket per router-facing address, multicast plus unicast to its gateway.</summary>
    internal static IReadOnlyList<SearchPlan> SystemPlans(INetworkView view)
    {
        var gateways = LanAddressPicker.Gateways(view.Interfaces());
        if (gateways.Count == 0)
        {
            return new[] { new SearchPlan(null, new[] { Multicast }) };
        }

        return gateways.Select(g => new SearchPlan(g.Local, new[] { Multicast, new IPEndPoint(g.Gateway, 1900) })).ToList();
    }

    public async Task<RouterSearchResult> FindAsync(CancellationToken ct)
    {
        var answers = await SearchAsync(ct).ConfigureAwait(false);
        if (answers.Count == 0)
        {
            return new RouterSearchResult(RouterStatus.NoReply, null);
        }

        var described = false;
        RouterSearchResult? mediaOnly = null;
        foreach (var answer in answers.OrderByDescending(a => a.SaysGateway))
        {
            var doc = await DescribeAsync(answer, ct).ConfigureAwait(false);
            if (doc is null)
            {
                continue;
            }

            described = true;
            var root = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "device");
            if (root is null)
            {
                continue;
            }

            var manufacturer = Text(root, "manufacturer");
            var model = FirstNonEmpty(Text(root, "modelName"), Text(root, "modelNumber"), Text(root, "friendlyName"));
            var services = WanServices(root, answer.Location).ToList();
            if (services.Count == 0)
            {
                if (answer.SaysGateway)
                {
                    mediaOnly ??= new RouterSearchResult(RouterStatus.NoPortService, null, manufacturer, model);
                }

                continue;
            }

            var service = await PickServiceAsync(services, ct).ConfigureAwait(false);
            return new RouterSearchResult(
                RouterStatus.Found,
                new IgdDevice(service, answer.From, manufacturer, model, AdminPage(root, answer.Location)),
                manufacturer,
                model);
        }

        return mediaOnly ?? new RouterSearchResult(described ? RouterStatus.NoPortService : RouterStatus.NoDescription, null);
    }

    private async Task<IgdService> PickServiceAsync(List<IgdService> services, CancellationToken ct)
    {
        if (services.Count == 1)
        {
            return services[0];
        }

        foreach (var s in services)
        {
            try
            {
                if (await new IgdClient(_http, s).GetExternalAddressAsync(ct).ConfigureAwait(false) is not null)
                {
                    return s;
                }
            }
            catch (Exception e) when (e is HttpRequestException or UpnpException or TaskCanceledException or FormatException or InvalidDataException)
            {
                // Try the next one; a router with a WANIP and a WANPPP service usually has only one connected.
            }

            ct.ThrowIfCancellationRequested();
        }

        return services[0];
    }

    private sealed record Answer(Uri Location, IPAddress From, bool SaysGateway);

    private async Task<List<Answer>> SearchAsync(CancellationToken ct)
    {
        var plans = _plans();
        var answers = new List<Answer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(_window);

        var sockets = new List<Socket>();
        try
        {
            foreach (var plan in plans)
            {
                var socket = Open(plan);
                if (socket is null)
                {
                    continue;
                }

                sockets.Add(socket);
                await SendAsync(socket, plan.Targets, window.Token).ConfigureAwait(false);
            }

            var readers = sockets.Select(s => ReadAsync(s, answers, seen, window)).ToList();
            try
            {
                await Task.WhenAll(readers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The window closed: that is how a search ends.
            }
        }
        finally
        {
            foreach (var s in sockets)
            {
                s.Dispose();
            }
        }

        ct.ThrowIfCancellationRequested();
        lock (answers)
        {
            return answers.ToList();
        }
    }

    private static Socket? Open(SearchPlan plan)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(plan.Bind ?? IPAddress.Any, 0));
            if (plan.Bind is not null)
            {
                // Bound is not enough for multicast on Windows: the interface it leaves by comes from the routing
                // table unless this is set, and a VPN usually holds the lowest metric.
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, plan.Bind.GetAddressBytes());
            }

            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            return socket;
        }
        catch (SocketException)
        {
            // An address that went away between listing and binding. The other plans still run.
            socket.Dispose();
            return null;
        }
    }

    private static async Task SendAsync(Socket socket, IReadOnlyList<IPEndPoint> targets, CancellationToken ct)
    {
        for (var round = 0; round < 2; round++)
        {
            foreach (var st in SearchTargets)
            {
                var message = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
                foreach (var target in targets)
                {
                    try
                    {
                        await socket.SendToAsync(message, SocketFlags.None, target, ct).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Multicast can be refused outright on an odd routing table; the unicast send may still work.
                    }
                }
            }
        }
    }

    private static async Task ReadAsync(Socket socket, List<Answer> answers, HashSet<string> seen, CancellationTokenSource window)
    {
        var ct = window.Token;
        var buffer = new byte[MaxDatagram];
        while (true)
        {
            lock (answers)
            {
                if (answers.Count >= MaxCandidates || answers.Any(a => a.SaysGateway))
                {
                    // Unambiguous, or enough: stop every reader, not just this one.
                    window.Cancel();
                    return;
                }
            }

            SocketReceiveFromResult got;
            try
            {
                got = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // Windows reports an ICMP "port unreachable" from a unicast send as a receive error. Keep listening.
                continue;
            }

            var from = ((IPEndPoint)got.RemoteEndPoint).Address;
            if (from.IsIPv4MappedToIPv6)
            {
                from = from.MapToIPv4();
            }

            var text = Encoding.ASCII.GetString(buffer, 0, got.ReceivedBytes);
            var location = Header(text, "LOCATION");
            if (location is null || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
                || !IPAddress.TryParse(uri.Host, out var host) || !host.Equals(from))
            {
                // Only plain http to the address that answered (see the class comment).
                continue;
            }

            var kind = (Header(text, "ST") ?? string.Empty) + " " + (Header(text, "USN") ?? string.Empty);
            lock (answers)
            {
                if (seen.Add(uri.AbsoluteUri))
                {
                    answers.Add(new Answer(uri, from, kind.Contains("InternetGatewayDevice", StringComparison.Ordinal) || kind.Contains("WANIPConnection", StringComparison.Ordinal) || kind.Contains("WANPPPConnection", StringComparison.Ordinal)));
                }
            }
        }
    }

    internal static string? Header(string message, string name)
    {
        foreach (var line in message.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(colon + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private async Task<XDocument?> DescribeAsync(Answer answer, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(IgdClient.CallTimeout);
            using var response = await _http.GetAsync(answer.Location, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return IgdClient.Parse(await IgdClient.ReadCappedAsync(response.Content, timeout.Token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // Depth-first through nested devices (IGD → WANDevice → WANConnectionDevice), ordered by ServicePreference.
    private static IEnumerable<IgdService> WanServices(XElement device, Uri location)
    {
        var found = new List<(int Rank, IgdService Service)>();
        foreach (var service in device.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = Text(service, "serviceType");
            var rank = Array.IndexOf(ServicePreference, type);
            if (rank < 0 || !Uri.TryCreate(location, Text(service, "controlURL"), out var control)
                || control.Scheme != Uri.UriSchemeHttp || !string.Equals(control.Host, location.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found.Add((rank, new IgdService(control, type)));
        }

        return found.OrderBy(f => f.Rank).Select(f => f.Service);
    }

    private static Uri? AdminPage(XElement device, Uri location)
    {
        var text = Text(device, "presentationURL");
        return text.Length > 0 && Uri.TryCreate(location, text, out var page) && (page.Scheme == Uri.UriSchemeHttp || page.Scheme == Uri.UriSchemeHttps) ? page : null;
    }

    private static string Text(XElement parent, string name)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty;
}

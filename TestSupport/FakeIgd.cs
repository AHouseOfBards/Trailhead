using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.Trailhead.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.TestSupport;

internal enum SsdpMode
{
    /// <summary>Answers every search for a type it has, and upnp:rootdevice.</summary>
    All,

    /// <summary>An IGDv2-only router: silent to every v1 search, and to upnp:rootdevice (a real report, RASA).</summary>
    V2Only,

    /// <summary>UPnP off: no answer at all.</summary>
    Silent,

    /// <summary>Answers with a datagram that has no LOCATION.</summary>
    NoLocation,
}

internal sealed record FakeIgdCall(string Action, IReadOnlyDictionary<string, string> Args);

internal sealed record FakeMapping(int External, string Protocol, int Internal, string Client, string Description, int Lease, DateTimeOffset Added);

/// <summary>
/// A UPnP Internet Gateway Device on real sockets: SSDP over UDP on 127.0.0.1 (answering unicast M-SEARCH, since a
/// test can't rely on loopback multicast) and the description plus SOAP over a real Kestrel. It keeps its mapping
/// table in memory and behaves like miniupnpd where it matters: 718 for another client's port, leases counted down,
/// <c>NewLeaseDuration</c> read back as what is left. Every bad-router behaviour Trailhead must survive is a switch.
/// </summary>
internal sealed class FakeIgd : IAsyncDisposable
{
    internal const string V1 = "urn:schemas-upnp-org:service:WANIPConnection:1";
    internal const string V2 = "urn:schemas-upnp-org:service:WANIPConnection:2";

    private readonly WebApplication _app;
    private readonly Socket _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _ssdp;
    private readonly object _gate = new();
    private readonly Dictionary<(int, string), FakeMapping> _mappings = new();
    private readonly List<FakeIgdCall> _calls = new();
    private readonly List<string> _searches = new();
    private int _generation = 1;

    private FakeIgd(WebApplication app, Uri http, Socket udp)
    {
        _app = app;
        Http = http;
        _udp = udp;
        _ssdp = Task.Run(AnswerSearchesAsync);
    }

    internal Uri Http { get; }

    internal IPEndPoint Ssdp => (IPEndPoint)_udp.LocalEndPoint!;

    /// <summary>The search a test hands to <see cref="RouterFinder"/>: bound to loopback, sent to this fake only.</summary>
    internal SearchPlan Plan => new(IPAddress.Loopback, new[] { Ssdp });

    internal SsdpMode Mode { get; set; } = SsdpMode.All;

    /// <summary>The service the description offers: <see cref="V1"/>, <see cref="V2"/> or WANPPPConnection:1.</summary>
    internal string ServiceType { get; set; } = V1;

    /// <summary>Replaces the whole description (an empty one, one with no WAN service).</summary>
    internal string? DescriptionOverride { get; set; }

    internal string Manufacturer { get; set; } = "FakeRouter Inc.";

    internal string Model { get; set; } = "FR-1000";

    internal string WanAddress { get; set; } = "203.0.113.7";

    /// <summary>724 for any add whose external and internal ports differ.</summary>
    internal bool SamePortRequired { get; set; }

    /// <summary>The code a permanent (0) lease is refused with: 725, 402 or 501. Null: permanent is fine.</summary>
    internal int? RefusePermanentWith { get; set; }

    /// <summary>Refuse any lease longer than this with 402 (a router that caps lower than a week).</summary>
    internal int? MaxLease { get; set; }

    /// <summary>Accept the lease asked for but store this one instead (a router that grants less than asked).</summary>
    internal int? GrantLease { get; set; }

    /// <summary>Answer every add with OK and store nothing.</summary>
    internal bool StoreNothing { get; set; }

    /// <summary>Report the granted lease on every read instead of counting down (some routers do).</summary>
    internal bool LeaseDoesNotCountDown { get; set; }

    /// <summary>GetSpecificPortMappingEntry answers HTTP 500 with no UPnP fault (a router that can't read entries).</summary>
    internal bool ReadsFail { get; set; }

    /// <summary>Runs when an AddPortMapping arrives, before it is handled: what the caller had written down by then.</summary>
    internal Action? OnAdd { get; set; }

    /// <summary>The clock leases count down on. Tests move it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    internal IReadOnlyList<FakeIgdCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    internal IReadOnlyList<string> Searches
    {
        get
        {
            lock (_gate)
            {
                return _searches.ToList();
            }
        }
    }

    internal IReadOnlyList<FakeMapping> Mappings
    {
        get
        {
            lock (_gate)
            {
                return _mappings.Values.ToList();
            }
        }
    }

    internal int Adds => Calls.Count(c => c.Action == "AddPortMapping");

    internal int Deletes => Calls.Count(c => c.Action == "DeletePortMapping");

    /// <param name="ssdpAddress">Where SSDP answers from. 127.0.0.2 makes a device whose LOCATION (on 127.0.0.1) is
    /// on another host than the one answering: the description is real and fetchable, so only the finder's own
    /// check can refuse it.</param>
    internal static async Task<FakeIgd> StartAsync(IPAddress? ssdpAddress = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeIgd? fake = null;
        app.Run(ctx => fake!.HandleAsync(ctx));
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(ssdpAddress ?? IPAddress.Loopback, 0));
        fake = new FakeIgd(app, new Uri(address.TrimEnd('/') + "/"), udp);
        return fake;
    }

    /// <summary>Another device's mapping, already on the router.</summary>
    internal void Seed(int external, int internalPort, string client, string description = "Someone else", int lease = 0, string protocol = "TCP")
    {
        lock (_gate)
        {
            _mappings[(external, protocol)] = new FakeMapping(external, protocol, internalPort, client, description, lease, Clock());
        }
    }

    internal void Forget(int external, string protocol = "TCP")
    {
        lock (_gate)
        {
            _mappings.Remove((external, protocol));
        }
    }

    /// <summary>A router restart: every mapping lost, and the description and control URL move.</summary>
    internal void Restart()
    {
        lock (_gate)
        {
            _mappings.Clear();
            _generation++;
        }
    }

    internal Uri DescriptionUrl
    {
        get
        {
            lock (_gate)
            {
                return new Uri(Http, "igd" + _generation + "/desc.xml");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp.Dispose();
        try
        {
            await _ssdp.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AnswerSearchesAsync()
    {
        var buffer = new byte[2048];
        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult got;
            try
            {
                got = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, got.ReceivedBytes);
            var st = RouterFinder.Header(text, "ST") ?? string.Empty;
            lock (_gate)
            {
                _searches.Add(st);
            }

            if (!text.StartsWith("M-SEARCH", StringComparison.Ordinal) || !Answers(st))
            {
                continue;
            }

            var location = Mode == SsdpMode.NoLocation ? null : DescriptionUrl.AbsoluteUri;
            var reply = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: " + st + "\r\nUSN: uuid:fake-igd::" + st + "\r\nEXT:\r\nSERVER: Fake/1.0 UPnP/1.1 FakeIgd/1.0\r\n"
                + (location is null ? string.Empty : "LOCATION: " + location + "\r\n") + "\r\n";
            try
            {
                await _udp.SendToAsync(Encoding.ASCII.GetBytes(reply), SocketFlags.None, got.RemoteEndPoint, _stop.Token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
        }
    }

    private bool Answers(string st)
    {
        var version = ServiceType == V2 ? 2 : 1;
        return Mode switch
        {
            SsdpMode.Silent => false,
            SsdpMode.V2Only => st is V2 or "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            _ => st == "upnp:rootdevice" || st == ServiceType || st == "urn:schemas-upnp-org:device:InternetGatewayDevice:" + version,
        };
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        int generation;
        lock (_gate)
        {
            generation = _generation;
        }

        var prefix = "/igd" + generation + "/";
        if (!ctx.Request.Path.StartsWithSegments(prefix.TrimEnd('/'), StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        if (ctx.Request.Method == "GET" && ctx.Request.Path.Value!.EndsWith("/desc.xml", StringComparison.Ordinal))
        {
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.WriteAsync(DescriptionOverride ?? Description(prefix)).ConfigureAwait(false);
            return;
        }

        if (ctx.Request.Method != "POST" || !ctx.Request.Path.Value!.EndsWith("/ctl", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var soapAction = ctx.Request.Headers["SOAPAction"].ToString().Trim('"');
        var hash = soapAction.LastIndexOf('#');
        var action = hash >= 0 ? soapAction[(hash + 1)..] : soapAction;
        var service = hash >= 0 ? soapAction[..hash] : string.Empty;
        using var reader = new StreamReader(ctx.Request.Body);
        var body = XDocument.Parse(await reader.ReadToEndAsync().ConfigureAwait(false));
        var call = body.Descendants().FirstOrDefault(e => e.Name.LocalName == action);
        var args = call?.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value) ?? new Dictionary<string, string>();
        lock (_gate)
        {
            _calls.Add(new FakeIgdCall(action, args));
        }

        if (service != ServiceType || call is null || call.Name.NamespaceName != ServiceType)
        {
            await FaultAsync(ctx, 401, "Invalid Action").ConfigureAwait(false);
            return;
        }

        if (action == "AddPortMapping")
        {
            OnAdd?.Invoke();
        }

        var (code, fields) = Act(action, args);
        if (code != 0)
        {
            await FaultAsync(ctx, code, "Fake error " + code).ConfigureAwait(false);
            return;
        }

        if (code == 0 && fields is null)
        {
            ctx.Response.StatusCode = 500;
            return;
        }

        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        var inner = string.Concat(fields!.Select(f => "<" + f.Key + ">" + System.Security.SecurityElement.Escape(f.Value) + "</" + f.Key + ">"));
        await ctx.Response.WriteAsync(
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + "<u:" + action + "Response xmlns:u=\"" + ServiceType + "\">" + inner + "</u:" + action + "Response></s:Body></s:Envelope>").ConfigureAwait(false);
    }

    private (int Code, Dictionary<string, string>? Fields) Act(string action, Dictionary<string, string> a)
    {
        var none = new Dictionary<string, string>();
        lock (_gate)
        {
            switch (action)
            {
                case "GetExternalIPAddress":
                    return (0, new Dictionary<string, string> { ["NewExternalIPAddress"] = WanAddress });

                case "AddPortMapping":
                {
                    var external = int.Parse(a["NewExternalPort"], System.Globalization.CultureInfo.InvariantCulture);
                    var internalPort = int.Parse(a["NewInternalPort"], System.Globalization.CultureInfo.InvariantCulture);
                    var lease = int.Parse(a["NewLeaseDuration"], System.Globalization.CultureInfo.InvariantCulture);
                    var protocol = a["NewProtocol"];
                    if (SamePortRequired && external != internalPort)
                    {
                        return (UpnpCodes.SamePortRequired, null);
                    }

                    if (lease == 0 && RefusePermanentWith is { } refuse)
                    {
                        return (refuse, null);
                    }

                    if (MaxLease is { } max && (lease == 0 || lease > max))
                    {
                        return (UpnpCodes.InvalidArgs, null);
                    }

                    if (_mappings.TryGetValue((external, protocol), out var existing) && existing.Client != a["NewInternalClient"])
                    {
                        return (UpnpCodes.Conflict, null);
                    }

                    if (!StoreNothing)
                    {
                        _mappings[(external, protocol)] = new FakeMapping(external, protocol, internalPort, a["NewInternalClient"], a["NewPortMappingDescription"], GrantLease ?? lease, Clock());
                    }

                    return (0, none);
                }

                case "GetSpecificPortMappingEntry":
                {
                    if (ReadsFail)
                    {
                        return (0, null);
                    }

                    var key = (int.Parse(a["NewExternalPort"], System.Globalization.CultureInfo.InvariantCulture), a["NewProtocol"]);
                    if (!_mappings.TryGetValue(key, out var m))
                    {
                        return (UpnpCodes.NoSuchEntry, null);
                    }

                    var left = m.Lease;
                    if (m.Lease > 0 && !LeaseDoesNotCountDown)
                    {
                        left = m.Lease - (int)(Clock() - m.Added).TotalSeconds;
                        if (left <= 0)
                        {
                            // Lapsed, as miniupnpd removes it.
                            _mappings.Remove(key);
                            return (UpnpCodes.NoSuchEntry, null);
                        }
                    }

                    return (0, new Dictionary<string, string>
                    {
                        ["NewInternalPort"] = m.Internal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["NewInternalClient"] = m.Client,
                        ["NewEnabled"] = "1",
                        ["NewPortMappingDescription"] = m.Description,
                        ["NewLeaseDuration"] = left.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                }

                case "DeletePortMapping":
                {
                    var key = (int.Parse(a["NewExternalPort"], System.Globalization.CultureInfo.InvariantCulture), a["NewProtocol"]);
                    return _mappings.Remove(key) ? (0, none) : (UpnpCodes.NoSuchEntry, null);
                }

                default:
                    return (401, null);
            }
        }
    }

    private static Task FaultAsync(HttpContext ctx, int code, string description)
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        return ctx.Response.WriteAsync(
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><s:Fault>"
            + "<faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">"
            + "<errorCode>" + code + "</errorCode><errorDescription>" + description + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>");
    }

    private string Description(string prefix)
    {
        var version = ServiceType == V2 ? 2 : 1;
        return "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><specVersion><major>1</major><minor>1</minor></specVersion><device>"
            + "<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:" + version + "</deviceType><friendlyName>Fake router</friendlyName>"
            + "<manufacturer>" + Manufacturer + "</manufacturer><modelName>" + Model + "</modelName><presentationURL>/admin</presentationURL>"
            + "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:" + version + "</deviceType>"
            + "<serviceList><service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><controlURL>" + prefix + "common</controlURL></service></serviceList>"
            + "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:" + version + "</deviceType>"
            + "<serviceList><service><serviceType>" + ServiceType + "</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId><controlURL>" + prefix + "ctl</controlURL></service></serviceList>"
            + "</device></deviceList></device></deviceList></device></root>";
    }
}

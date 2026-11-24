using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>What the stand-in Jellyfin saw of one request, before its forwarded-headers middleware ran and after.</summary>
internal sealed record Seen(string Method, string PathAndQuery, Dictionary<string, string[]> Headers, string? Remote, string? RemoteAfter, long BodyBytes);

/// <summary>
/// A real Kestrel standing in for Jellyfin: the forwarded-headers middleware with options built the way Jellyfin
/// builds them at start (NetworkRig.LiveOptionsAsJellyfinBuildsThem), Trailhead's real echo controller behind it, and a
/// few routes for the tests. It records every request's headers as they arrived on the wire.
/// </summary>
internal sealed class FakeJellyfin : IAsyncDisposable
{
    internal const string Version = "12.4.0-test";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<Seen> _seen = new();

    private FakeJellyfin(WebApplication app, int port, ForwardedHeadersOptions options)
    {
        _app = app;
        Port = port;
        Options = options;
    }

    internal int Port { get; }

    internal ForwardedHeadersOptions Options { get; }

    /// <summary>Bytes the big download has written so far (counted after each write returns).</summary>
    internal long BigWritten;

    internal int SignIns;

    internal IReadOnlyList<Seen> All => _seen.ToList();

    /// <summary>The last request seen. Throws when there was none, so a test can't pass on nothing.</summary>
    internal Seen Last(string pathPrefix)
    {
        var match = _seen.Where(s => s.PathAndQuery.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (match.Count == 0)
        {
            throw new InvalidOperationException("The stand-in Jellyfin saw no request for " + pathPrefix + ".");
        }

        return match[^1];
    }

    internal static async Task<FakeJellyfin> StartAsync(EdgeIdentity identity, string[] knownProxies)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(identity);
        builder.Services.AddControllers().AddApplicationPart(typeof(Plugin).Assembly);

        var network = new MediaBrowser.Common.Net.NetworkConfiguration { KnownProxies = knownProxies };
        var options = Settings.NetworkRig.LiveOptionsAsJellyfinBuildsThem(network);
        var app = builder.Build();
        FakeJellyfin? self = null;

        app.Use(async (ctx, next) =>
        {
            ctx.Items["raw"] = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? string.Empty).ToArray(), StringComparer.OrdinalIgnoreCase);
            ctx.Items["rawRemote"] = ctx.Connection.RemoteIpAddress?.ToString();
            await next().ConfigureAwait(false);
        });
        app.UseForwardedHeaders(options);
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            long body = 0;
            if (!ctx.WebSockets.IsWebSocketRequest && !ctx.Request.Path.StartsWithSegments("/Users"))
            {
                var buffer = new byte[16384];
                int n;
                while ((n = await ctx.Request.Body.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    body += n;
                }
            }

            self!._seen.Enqueue(new Seen(
                ctx.Request.Method,
                ctx.Request.Path + ctx.Request.QueryString,
                (Dictionary<string, string[]>)ctx.Items["raw"]!,
                (string?)ctx.Items["rawRemote"],
                ctx.Connection.RemoteIpAddress?.ToString(),
                body));
            await next().ConfigureAwait(false);
        });

        app.MapGet("/System/Info/Public", () => Results.Json(new Dictionary<string, string> { ["Version"] = Version, ["ServerName"] = "test" }));
        app.MapGet("/echo", (HttpContext ctx) => Results.Text("ok " + ctx.Connection.RemoteIpAddress));
        app.MapPost("/echo", (HttpContext ctx) => Results.Text("ok"));
        app.MapPost("/Users/AuthenticateByName", async (HttpContext ctx) =>
        {
            Interlocked.Increment(ref self!.SignIns);
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body).ConfigureAwait(false);
            var pw = doc.RootElement.TryGetProperty("Pw", out var p) ? p.GetString() : null;
            return pw == "right" ? Results.Json(new { AccessToken = "t" }) : Results.StatusCode(401);
        });
        app.MapGet("/big", async (HttpContext ctx) =>
        {
            // A long download written in 64 KB pieces; BigWritten says how far the writer has got.
            ctx.Response.ContentType = "application/octet-stream";
            var chunk = new byte[64 * 1024];
            var total = long.Parse(ctx.Request.Query["bytes"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
            for (long sent = 0; sent < total && !ctx.RequestAborted.IsCancellationRequested; sent += chunk.Length)
            {
                await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                Interlocked.Add(ref self!.BigWritten, chunk.Length);
            }
        });
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var buffer = new byte[1024];
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            await ws.SendAsync(new ArraySegment<byte>(buffer, 0, r.Count), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        });
        app.MapControllers();

        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeJellyfin(app, new Uri(address).Port, options);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A lifetime the test drives: Started, then Stopping and Stopped.</summary>
internal sealed class DrivenLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    internal void Start() => _started.Cancel();

    public void StopApplication()
    {
        _stopping.Cancel();
        _stopped.Cancel();
    }
}

/// <summary>Certificates for Edge: a leaf with its key and an intermediate, as the store would hand them over.</summary>
internal static class EdgeCerts
{
    /// <summary>Every intermediate Edge's tests make has a name starting with this.</summary>
    internal const string IntermediatePrefix = "Trailhead Edge Test Intermediate ";

    internal static StoredCertificate Make(string name = "trailhead-test.example.com")
    {
        // A name of its own each time: on Windows the TLS context puts the intermediate in the user's CA store
        // (seen: 101 "Test Intermediate R1" there after a few runs, each with a different key), and Windows' chain
        // builder then failed every other test's chain with "An unknown chain building error occurred".
        var (pem, key, _) = TestPki.Issue(name, "Test Root X1", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(60), intermediateCn: IntermediatePrefix + Guid.NewGuid().ToString("N"));
        var certs = new List<X509Certificate2>();
        var span = pem.AsSpan();
        while (PemEncoding.TryFind(span, out var fields))
        {
            certs.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(span[fields.Base64Data].ToString())));
            span = span[fields.Location.End..];
        }

        using var withKey = certs[0].CopyWithPrivateKey(key);

        // The same round trip the store's LoadForServing does, so SChannel gets a key it can use.
        var leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "x"), "x", OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        return new StoredCertificate(leaf, new[] { leaf }.Concat(certs.Skip(1)).ToList());
    }

    /// <summary>Takes the tests' intermediates back out of the user's CA store (Windows only).</summary>
    internal static void CleanUserStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Where(c => c.Subject.StartsWith("CN=" + IntermediatePrefix, StringComparison.Ordinal)).ToList())
            {
                store.Remove(c);
            }
        }
        catch (CryptographicException)
        {
        }
    }
}

/// <summary>Free loopback ports, found by binding port 0 and letting go.</summary>
internal static class FreePorts
{
    internal static int[] Take(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ =>
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return l;
        }).ToList();
        var ports = listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        listeners.ForEach(l => l.Stop());
        return ports;
    }

    /// <summary>Holds a port the way another program would, on every address.</summary>
    internal static Socket Hold(int port)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        if (OperatingSystem.IsWindows())
        {
            s.ExclusiveAddressUse = true;
        }

        s.Bind(new IPEndPoint(IPAddress.Loopback, port));
        s.Listen();
        return s;
    }

    internal static bool IsFree(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>
/// Edge in front of a stand-in Jellyfin: the real <see cref="EdgeService"/> with its real listener, pipeline and
/// self-test, a test certificate, and a journal and port table in a temp folder.
/// </summary>
internal sealed class EdgeRig : IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ILoggerFactory _loggers;

    private EdgeRig(FakeJellyfin jellyfin, EdgeIdentity identity, ListLogger log)
    {
        Jellyfin = jellyfin;
        Identity = identity;
        Log = log;
        _loggers = LoggerFactory.Create(b => b.AddProvider(new ListLoggerProvider(log)).SetMinimumLevel(LogLevel.Trace));
        Journal = new Journal(() => _folder.File("journal.json"), TimeProvider.System, log);
        Ports = new PortTable(() => _folder.File("ports.json"), Journal);
    }

    internal FakeJellyfin Jellyfin { get; }

    internal EdgeIdentity Identity { get; }

    internal ListLogger Log { get; }

    internal Journal Journal { get; }

    internal PortTable Ports { get; }

    internal string Folder => _folder.Path;

    internal EdgeService? Service { get; private set; }

    internal DrivenLifetime? Lifetime { get; private set; }

    internal PluginConfiguration Config { get; } = new() { Hostname = "trailhead-test.example.com" };

    internal StoredCertificate? Certificate { get; set; } = EdgeCerts.Make();

    internal IPAddress? Forwarding { get; set; } = IPAddress.Loopback;

    /// <summary>The live options Edge reads. Default: the stand-in's own, the ones it really applies.</summary>
    internal Func<ForwardedHeadersOptions?>? Live { get; set; }

    internal int Port => Service?.Port ?? throw new InvalidOperationException("Edge isn't running.");

    internal static async Task<EdgeRig> StartAsync(string[]? knownProxies = null)
    {
        var identity = new EdgeIdentity();
        var jellyfin = await FakeJellyfin.StartAsync(identity, knownProxies ?? new[] { "127.0.0.1" }).ConfigureAwait(false);
        return new EdgeRig(jellyfin, identity, new ListLogger());
    }

    internal EdgeOptions Options(int[]? ports = null, int? extra = null, ThrottleOptions? throttle = null, bool anyAddress = false)
        => new()
        {
            Ports = ports ?? FreePorts.Take(2),
            ExtraPort = extra,
            ListenAddress = anyAddress ? null : IPAddress.Loopback,
            BindRetry = TimeSpan.FromMilliseconds(300),
            BindRetryStep = TimeSpan.FromMilliseconds(50),
            RecheckOpen = TimeSpan.FromHours(1),
            RecheckClosed = TimeSpan.FromHours(1),
            RetryAfter = TimeSpan.FromSeconds(30),
            Throttle = throttle ?? new ThrottleOptions(),
        };

    internal EdgeEnvironment Environment() => new()
    {
        Config = () => Config,
        JellyfinPort = () => Jellyfin.Port,
        BaseUrl = () => string.Empty,
        Forwarding = () => Forwarding,
        Live = () => (Live ?? (() => Jellyfin.Options))(),
        Version = () => FakeJellyfin.Version,
        Certificate = () => Certificate,
        HasCertificate = () => Certificate is not null,
    };

    /// <summary>Builds the service, runs StartAsync and ApplicationStarted, and waits for Edge's start (and first gate check).</summary>
    internal async Task<EdgeService> StartEdgeAsync(EdgeOptions? options = null)
    {
        Lifetime = new DrivenLifetime();
        Service = new EdgeService(_loggers, Lifetime, Identity, Ports, null, Environment, options ?? Options(), TimeProvider.System);
        await Service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Lifetime.Start();
        await Service.StartTask.ConfigureAwait(false);
        if (Service.IsRunning)
        {
            await Service.CheckGateAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return Service;
    }

    internal async Task StopEdgeAsync()
    {
        if (Service is null || Lifetime is null)
        {
            return;
        }

        Lifetime.StopApplication();
        await Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Service.Dispose();
    }

    /// <summary>A client of Edge from <paramref name="source"/> that trusts Edge's certificate and records each thumbprint it saw.</summary>
    internal EdgeClient Client(string source = "127.0.0.5", Version? version = null) => new(Port, IPAddress.Parse(source), version);

    public async ValueTask DisposeAsync()
    {
        await StopEdgeAsync().ConfigureAwait(false);
        await Jellyfin.DisposeAsync().ConfigureAwait(false);
        _loggers.Dispose();
        _folder.Dispose();
        EdgeCerts.CleanUserStore();
    }
}

/// <summary>An HTTPS client of Edge from a chosen loopback source address.</summary>
internal sealed class EdgeClient : IDisposable
{
    private readonly SocketsHttpHandler _handler;

    internal EdgeClient(int port, IPAddress source, Version? version)
    {
        Port = port;
        _handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (ctx, ct) =>
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Bind(new IPEndPoint(source, 0));
                await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct).ConfigureAwait(false);
                return new NetworkStream(s, true);
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, chain, _) =>
                {
                    Thumbprints.Enqueue(cert!.GetCertHashString());
                    SentChains.Enqueue(chain!.ChainPolicy.ExtraStore.Cast<X509Certificate2>().Select(c => c.Subject).ToArray());
                    return true;
                },
            },
        };
        Http = new HttpClient(_handler) { BaseAddress = new Uri("https://127.0.0.1:" + port + "/"), Timeout = TimeSpan.FromSeconds(60) };
        if (version is not null)
        {
            Http.DefaultRequestVersion = version;
            Http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
    }

    internal int Port { get; }

    internal HttpClient Http { get; }

    internal ConcurrentQueue<string> Thumbprints { get; } = new();

    internal ConcurrentQueue<string[]> SentChains { get; } = new();

    public void Dispose() => Http.Dispose();
}

/// <summary>Raw TLS to Edge, for what an HTTP client library would tidy away (the Connection header).</summary>
internal static class RawTls
{
    internal static async Task<string> SendAsync(int port, string request, int responses = 1)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "trailhead-test.example.com", ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 } }).ConfigureAwait(false);
        await tls.WriteAsync(Encoding.ASCII.GetBytes(request)).ConfigureAwait(false);
        await tls.FlushAsync().ConfigureAwait(false);
        var buffer = new byte[65536];
        var text = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            int n;
            while ((n = await tls.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                // The stand-in's /echo body starts with "ok"; stop once each answer's body has started.
                if (text.ToString().Split("\r\n\r\nok").Length - 1 >= responses)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return text.ToString();
    }
}

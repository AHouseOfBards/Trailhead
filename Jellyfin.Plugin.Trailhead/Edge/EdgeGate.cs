using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>Why the gate is shut, or that it is open. Codes, not text: the status page looks the words up.</summary>
internal static class GateReasons
{
    internal const string Open = "open";
    internal const string Checking = "checking";

    /// <summary>Jellyfin's live forwarded-headers options don't trust the forwarding address (needs a restart).</summary>
    internal const string NotTrusted = "not_trusted";

    /// <summary>Jellyfin's saved bind addresses now give a different forwarding address than the one Edge uses.</summary>
    internal const string ForwardingChanged = "forwarding_changed";

    /// <summary>A request through Edge didn't come back with Jellyfin's version.</summary>
    internal const string NoJellyfin = "no_jellyfin";

    /// <summary>Jellyfin saw Edge's address, not the client's.</summary>
    internal const string AddressNotPassed = "address_not_passed";

    /// <summary>The self-test couldn't run (no test address, or Edge didn't answer).</summary>
    internal const string SelfTestFailed = "self_test_failed";
}

/// <summary>The gate as last checked.</summary>
internal sealed record GateState(bool IsOpen, string Reason, DateTimeOffset At);

/// <summary>
/// The first half of the gate (T29, 03 §2b): do Jellyfin's <b>live</b> forwarded-headers options trust the forwarding
/// address? Not the saved Known proxies: Jellyfin builds the options once at start (Jellyfin.Server/Startup.cs:73,
/// ApiServiceCollectionExtensions.cs:116, v12.4), so a value saved since isn't in effect until a restart.
/// </summary>
internal static class LiveTrust
{
    internal static bool Trusts(ForwardedHeadersOptions? options, IPAddress forwarding)
    {
        if (options is null || (options.ForwardedHeaders & ForwardedHeaders.XForwardedFor) == 0)
        {
            // Jellyfin turns forwarding off when Known proxies was empty at start (ApiServiceCollectionExtensions.cs:171-176).
            return false;
        }

        var a = Normal(forwarding);
        return options.KnownProxies.Any(p => Normal(p).Equals(a)) || options.KnownIPNetworks.Any(n => n.Contains(a));
    }

    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

internal sealed record SelfTestResult(bool Passed, string Reason);

/// <summary>
/// The second half of the gate: a request through Edge, from a test address that isn't the forwarding address, to
/// <c>/System/Info/Public</c> (must give Jellyfin's version) and to Trailhead's echo (must give the test address, so
/// the client's address really reached Jellyfin). It talks TLS to Edge like any client, and trusts only the
/// certificate Edge is serving now.
/// </summary>
internal sealed class SelfTest
{
    private readonly Func<IPEndPoint> _edge;
    private readonly IPAddress _source;
    private readonly string _probeToken;
    private readonly string _basePath;
    private readonly Func<string?> _thumbprint;
    private readonly string? _version;
    private readonly TimeSpan _timeout;

    internal SelfTest(Func<IPEndPoint> edge, IPAddress source, string probeToken, string basePath, Func<string?> thumbprint, string? version, TimeSpan timeout)
    {
        _edge = edge;
        _source = source;
        _probeToken = probeToken;
        _basePath = basePath;
        _thumbprint = thumbprint;
        _version = version;
        _timeout = timeout;
    }

    /// <summary>
    /// The address the self-test connects from: in 127.0.0.0/8, which every host has on loopback (Windows and Linux
    /// alike), so it needs no network and is never the forwarding address (which is 127.0.0.1, ::1 or a LAN address).
    /// </summary>
    internal static IPAddress SourceFor(IPAddress forwarding)
        => forwarding.Equals(IPAddress.Parse("127.0.0.2")) ? IPAddress.Parse("127.0.0.3") : IPAddress.Parse("127.0.0.2");

    internal static IPEndPoint LoopbackOf(IPAddress? listen, int port)
        => new(listen is null || listen.Equals(IPAddress.Any) || listen.Equals(IPAddress.IPv6Any) ? IPAddress.Loopback : listen, port);

    /// <summary>Never throws except <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.</summary>
    internal async Task<SelfTestResult> RunAsync(CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = ConnectAsync,
                SslOptions = new SslClientAuthenticationOptions
                {
                    // Edge's own certificate, by thumbprint: the name is for outside clients, this runs on loopback.
                    RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && _thumbprint() is { } t
                        && string.Equals(cert.GetCertHashString(), t, StringComparison.OrdinalIgnoreCase),
                },
            };
            using var client = new HttpClient(handler) { Timeout = _timeout };
            var origin = "https://" + _edge() + _basePath;

            using (var info = await SendAsync(client, origin + "/System/Info/Public", ct).ConfigureAwait(false))
            {
                if (!info.IsSuccessStatusCode)
                {
                    return new SelfTestResult(false, GateReasons.NoJellyfin);
                }

                using var doc = JsonDocument.Parse(await info.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var version = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Version", out var v) ? v.GetString() : null;
                if (string.IsNullOrEmpty(version) || (_version is not null && !string.Equals(version, _version, StringComparison.Ordinal)))
                {
                    return new SelfTestResult(false, GateReasons.NoJellyfin);
                }
            }

            var nonce = Guid.NewGuid().ToString("N");
            using var echo = await SendAsync(client, origin + "/Trailhead/Edge/Echo?n=" + nonce, ct).ConfigureAwait(false);
            var text = echo.IsSuccessStatusCode ? await echo.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : string.Empty;
            var parts = text.Trim().Split(' ');
            if (parts.Length != 2 || parts[0] != nonce || !IPAddress.TryParse(parts[1], out var seen))
            {
                return new SelfTestResult(false, GateReasons.AddressNotPassed);
            }

            return seen.Equals(_source) ? new SelfTestResult(true, GateReasons.Open) : new SelfTestResult(false, GateReasons.AddressNotPassed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new SelfTestResult(false, GateReasons.SelfTestFailed);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(EdgeTransformer.ProbeHeader, _probeToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var target = _edge();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // The source address is the point of the test: Jellyfin must report this one, not the forwarding address.
            socket.Bind(new IPEndPoint(_source, 0));
            await socket.ConnectAsync(target.Address.AddressFamily == AddressFamily.InterNetwork ? target : new IPEndPoint(IPAddress.Loopback, target.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>What one request through Edge needs to know about this start.</summary>
internal sealed record ProxySettings(IPAddress Forwarding, int JellyfinPort, string BasePath, TimeSpan ActivityTimeout, TimeSpan RetryAfter);

/// <summary>
/// Edge's request pipeline, shared by every port it listens on: the gate, the login throttle, then YARP's forwarder to
/// Jellyfin's own HTTP port at the forwarding address. Bodies are streamed both ways and never rewritten.
/// </summary>
internal sealed class EdgeProxy : IAsyncDisposable
{
    internal const string EchoPath = "/Trailhead/Edge/Echo";

    // A sign-in body is a name and a password. A bigger one isn't read for the name; it is still throttled, as "".
    private const int MaxSignInBody = 64 * 1024;

    private readonly ServiceProvider _services;
    private readonly HttpMessageInvoker _invoker;
    private readonly IHttpForwarder _forwarder;
    private readonly EdgeTransformer _transformer;
    private readonly ForwarderRequestConfig _config;
    private readonly ProxySettings _settings;
    private readonly EdgeIdentity _identity;
    private readonly Func<GateState> _gate;
    private readonly LoginThrottle _throttle;
    private readonly ILogger _logger;
    private readonly string _prefix;

    internal EdgeProxy(ProxySettings settings, EdgeIdentity identity, Func<GateState> gate, LoginThrottle throttle, ILoggerFactory edgeLoggers, ILogger logger)
    {
        _settings = settings;
        _identity = identity;
        _gate = gate;
        _throttle = throttle;
        _logger = logger;

        // YARP's forwarder in a container of its own: Jellyfin's would be the wrong lifetime, and Edge's Kestrel hosts
        // (one per port) shouldn't each have one.
        var collection = new ServiceCollection();
        // Before AddLogging, which only adds a factory when there is none. An instance, so disposing this container
        // leaves Jellyfin's loggers alone.
        collection.AddSingleton<ILoggerFactory>(edgeLoggers);
        collection.AddLogging();
        collection.AddHttpForwarder();
        _services = collection.BuildServiceProvider();
        _forwarder = _services.GetRequiredService<IHttpForwarder>();

        var forwarding = settings.Forwarding;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ActivityHeadersPropagator = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            EnableMultipleHttp2Connections = true,

            // From the forwarding address, explicitly: that is the address Jellyfin trusts. Left to the OS, a
            // connection to a LAN address could leave from another one on a host with two.
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(forwarding.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(forwarding, 0));
                    await socket.ConnectAsync(new IPEndPoint(forwarding, settings.JellyfinPort), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        _transformer = new EdgeTransformer(() => identity.Marker);
        _config = new ForwarderRequestConfig { ActivityTimeout = settings.ActivityTimeout };
        var host = forwarding.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + forwarding + "]" : forwarding.ToString();
        _prefix = "http://" + host + ":" + settings.JellyfinPort.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One request. Never throws: an error ends that request only.</summary>
    internal async Task HandleAsync(HttpContext ctx)
    {
        LoginThrottle.Ticket? ticket = null;
        try
        {
            // First, for every request: one entry per request, in order, or the next request would get this one's.
            var named = ConnectionNames.ForRequest(ctx, out var scanned);
            if (scanned && named is null)
            {
                // The connection's bytes couldn't be followed, so its Connection header couldn't be checked.
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.Headers.Connection = "close";
                return;
            }

            ctx.Items[EdgeTransformer.RequestItemKey] = named;
            // Listening on any address is one dual-mode socket, so an IPv4 client shows up as ::ffff:a.b.c.d, and
            // IsLoopback says false for ::ffff:127.0.0.2: the self-test was refused as a stranger on the first ts1 run.
            var remote = ctx.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : ctx.Connection.RemoteIpAddress;
            var probe = remote is not null && IPAddress.IsLoopback(remote) && ctx.Request.Headers[EdgeTransformer.ProbeHeader] is { Count: 1 } sent && _identity.IsProbe(sent[0]);

            if (!probe && IsPath(ctx.Request.Path.Value, EchoPath))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!probe && !_gate().IsOpen)
            {
                await RefuseAsync(ctx, StatusCodes.Status503ServiceUnavailable, _settings.RetryAfter, "edge.gate.closed").ConfigureAwait(false);
                return;
            }

            var route = SignInRoutes.Match(ctx.Request.Method, ctx.Request.Path.Value, _settings.BasePath, out var userId);
            if (route != SignInRoute.None && remote is not null)
            {
                var name = route switch
                {
                    SignInRoute.ById => "#id:" + userId,
                    SignInRoute.QuickConnect => "#quickconnect",
                    _ => await ReadUserNameAsync(ctx.Request).ConfigureAwait(false),
                };
                ticket = _throttle.Enter(remote, name);
                if (ticket.IsRefused)
                {
                    var wait = ticket.Wait;
                    ticket = null;
                    await RefuseAsync(ctx, StatusCodes.Status429TooManyRequests, wait, "edge.throttle.tooMany").ConfigureAwait(false);
                    return;
                }

                if (ticket.Wait > TimeSpan.Zero)
                {
                    await Task.Delay(ticket.Wait, ctx.RequestAborted).ConfigureAwait(false);
                }
            }

            var error = await _forwarder.SendAsync(ctx, _prefix, _invoker, _config, _transformer).ConfigureAwait(false);
            ticket?.Finish(error == ForwarderError.None ? SignInRoutes.Outcome(ctx.Response.StatusCode) : SignInOutcome.Neither);
            ticket = null;
            if (error != ForwarderError.None && error is not (ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled or ForwarderError.ResponseBodyCanceled or ForwarderError.UpgradeRequestCanceled or ForwarderError.UpgradeResponseCanceled))
            {
                _logger.LogDebug("Trailhead's listener couldn't pass a request on ({Error})", error);
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // The client went away while it waited.
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's listener failed on a request ({Error})", e.GetType().Name);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
        finally
        {
            ticket?.Finish(SignInOutcome.Neither);
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="route"/>, with or without the base URL in front.</summary>
    internal bool IsPath(string? path, string route)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var p = path.TrimEnd('/');
        var prefix = _settings.BasePath.TrimEnd('/');
        if (prefix.Length > 0 && p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            p = p[prefix.Length..];
        }

        return p.Equals(route, StringComparison.OrdinalIgnoreCase);
    }

    // The user name in an AuthenticateByName body ({"Username": ..., "Pw": ...}, read case-insensitively as Jellyfin
    // does). The body is buffered so YARP still sends all of it; one without a length, or too big, isn't read.
    private static async Task<string> ReadUserNameAsync(HttpRequest request)
    {
        if (request.ContentLength is not { } length || length is <= 0 or > MaxSignInBody)
        {
            return string.Empty;
        }

        request.EnableBuffering(MaxSignInBody, MaxSignInBody);
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, default, request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.Equals("Username", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    {
                        return p.Value.GetString() ?? string.Empty;
                    }
                }
            }

            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static async Task RefuseAsync(HttpContext ctx, int status, TimeSpan retryAfter, string key)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var text = UiText.Table.Get(key, UiText.FromAcceptLanguage(UiText.Table, ctx.Request.Headers.AcceptLanguage.ToString()));
        await ctx.Response.WriteAsync(text, ctx.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _invoker.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}

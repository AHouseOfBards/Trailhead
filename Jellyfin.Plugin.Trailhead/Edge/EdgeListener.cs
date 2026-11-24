using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>
/// One Kestrel, on one port, serving TLS with the certificate in the slot and handing every request to the shared
/// pipeline. Built on <c>WebApplication.CreateEmptyBuilder</c> with a lifetime that does nothing: the default builders
/// hook Ctrl+C, SIGTERM and ProcessExit and read environment variables and appsettings.json, all of which belong to
/// Jellyfin (spike T-S1 point 4). Kestrel keeps its defaults, as Jellyfin's does (it sets no limits of its own).
/// </summary>
internal sealed class EdgeListener : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EdgeListener(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    internal int Port { get; }

    /// <summary>
    /// Whether the port can be bound now, by binding it for a moment. On Windows exclusively: a plain bind to the
    /// wildcard succeeds next to another program's bind on one address, and that program would then get the
    /// connections to that address. Asked before Kestrel is, so a port in use is one quiet answer, not the inner host's
    /// "Hosting failed to start" with a stack trace (spike point 6).
    /// </summary>
    internal static bool CanBind(IPAddress? address, int port)
    {
        try
        {
            using var socket = NewSocket(address);
            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
            }

            socket.Bind(new IPEndPoint(address ?? (socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any), port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Starts Kestrel on the port. Null when the port can't be bound; anything else is thrown.</summary>
    internal static async Task<EdgeListener?> TryStartAsync(IPAddress? address, int port, RequestDelegate handler, CertificateSlot slot, ILoggerFactory edgeLoggers, CancellationToken ct)
    {
        if (!CanBind(address, port))
        {
            return null;
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = "Trailhead.Edge",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSingleton<IHostLifetime, NoLifetime>();
        builder.Services.AddSingleton<ILoggerFactory>(edgeLoggers);
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
        {
            // Jellyfin's own answer carries Jellyfin's Server header already.
            k.AddServerHeader = false;

            void Tls(ListenOptions listen)
            {
                // HTTP/3 stays off: it needs msquic and a forwarded UDP port (CleanStream, #17).
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(new TlsHandshakeCallbackOptions
                {
                    OnConnection = _ =>
                    {
                        // A missing certificate fails this handshake only; Edge doesn't start without one anyway.
                        var current = slot.ForHandshake() ?? throw new InvalidOperationException("no certificate");
                        return new ValueTask<SslServerAuthenticationOptions>(new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = current.Context,
                            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                        });
                    },
                });

                // After TLS, so it reads the plain request bytes: Kestrel hides the names a Connection header nominates.
                listen.Use(ConnectionNames.Middleware);
            }

            if (address is null)
            {
                // [::] in dual mode, which takes IPv4 too, or 0.0.0.0 when the system has no IPv6.
                k.ListenAnyIP(port, Tls);
            }
            else
            {
                k.Listen(address, port, Tls);
            }
        });

        var app = builder.Build();
        app.Run(handler);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            // Taken between the check and the bind.
            await app.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new EdgeListener(app, port);
    }

    /// <summary>Stops listening and lets go of the port. Open requests get a few seconds to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping is best effort; disposing below closes the sockets either way.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static Socket NewSocket(IPAddress? address)
    {
        if (address is not null)
        {
            return new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        if (Socket.OSSupportsIPv6)
        {
            return new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        }

        return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    private sealed class NoLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

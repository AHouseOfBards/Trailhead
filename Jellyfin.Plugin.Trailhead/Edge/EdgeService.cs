using System.Net;
using Campground.Shared.Contracts;
using Campground.Shared.Registry;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>Why Edge isn't running. Codes, not text.</summary>
internal static class EdgeNotRunning
{
    internal const string NotStarted = "not_started";
    internal const string NotSetUp = "not_set_up";
    internal const string NoCertificate = "no_certificate";
    internal const string NoForwardingAddress = "no_forwarding_address";
    internal const string NoJellyfinPort = "no_jellyfin_port";
    internal const string NoFreePort = "no_free_port";
    internal const string JournalUnavailable = "journal_unavailable";
    internal const string StartFailed = "start_failed";
    internal const string Stopping = "stopping";
}

/// <summary>
/// What Edge reads from Jellyfin and from Trailhead, as functions so a test can stand in for each. Every one is asked
/// when Edge starts, not when this is made, and any of them may throw: Edge then stays off (fail open).
/// </summary>
internal sealed class EdgeEnvironment
{
    internal required Func<PluginConfiguration> Config { get; init; }

    /// <summary>The HTTP port Jellyfin's Kestrel bound at start (<c>IServerApplicationHost.HttpPort</c>).</summary>
    internal required Func<int> JellyfinPort { get; init; }

    /// <summary>Jellyfin's HTTPS port when it listens on one, so Edge never takes it.</summary>
    internal Func<int?> JellyfinHttpsPort { get; init; } = () => null;

    internal required Func<string> BaseUrl { get; init; }

    internal required Func<IPAddress?> Forwarding { get; init; }

    /// <summary>Jellyfin's live forwarded-headers options, as built at start.</summary>
    internal required Func<ForwardedHeadersOptions?> Live { get; init; }

    /// <summary>The version <c>/System/Info/Public</c> must answer with. Null: any non-empty one.</summary>
    internal Func<string?> Version { get; init; } = () => null;

    internal required Func<StoredCertificate?> Certificate { get; init; }

    /// <summary>True when there is a certificate file, without loading it.</summary>
    internal required Func<bool> HasCertificate { get; init; }
}

/// <summary>
/// Owns Edge (03 §2, T2): the TLS listener in front of Jellyfin. It starts on <c>ApplicationStarted</c>, never in
/// <see cref="StartAsync"/> (a plugin's hosted services start before Jellyfin's own Kestrel, and an exception there could
/// take the start down, spike point 5), and only once setup has a name and a certificate. On
/// <c>ApplicationStopping</c> it stops and is awaited, so the ports are free before an in-process restart's new copy
/// binds (Jellyfin.Server/Program.cs loops StartServer, v12.4). Every step fails open: Jellyfin runs on whatever
/// happens here, and nothing here ever touches Jellyfin's own port.
/// </summary>
public sealed class EdgeService : IHostedService, IDisposable
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggers;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly EdgeIdentity _identity;
    private readonly PortTable _ports;
    private readonly CertificateServices? _certificates;
    private readonly Func<EdgeEnvironment> _environment;
    private readonly EdgeOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CertificateSlot _slot = new();
    private readonly LoginThrottle _throttle;

    private Running? _running;
    private volatile string _notRunning = EdgeNotRunning.NotStarted;
    private GateState _gateState;
    private Task? _startTask;
    private Task? _stopTask;
    private bool _extraLogged;

    /// <summary>Jellyfin builds this one. Jellyfin's services are looked up when Edge starts, so a missing one stops Edge only.</summary>
    public EdgeService(IServiceProvider services, ILoggerFactory loggers, IHostApplicationLifetime lifetime, EdgeIdentity identity, CertificateServices certificates)
        : this(
            loggers,
            lifetime,
            identity,
            services.GetRequiredService<PortTable>(),
            certificates,
            () => FromJellyfin(services, certificates),
            new EdgeOptions(),
            TimeProvider.System)
    {
    }

    internal EdgeService(ILoggerFactory loggers, IHostApplicationLifetime lifetime, EdgeIdentity identity, PortTable ports, CertificateServices? certificates, Func<EdgeEnvironment> environment, EdgeOptions options, TimeProvider time)
    {
        _loggers = loggers;
        _logger = loggers.CreateLogger<EdgeService>();
        _lifetime = lifetime;
        _identity = identity;
        _ports = ports;
        _certificates = certificates;
        _environment = environment;
        _options = options;
        _time = time;
        _throttle = new LoginThrottle(options.Throttle, time);
        _gateState = new GateState(false, GateReasons.Checking, time.GetUtcNow());
    }

    /// <summary>The port Edge listens on, from the reservation table. Null while it isn't running.</summary>
    internal int? Port => _running?.Main.Port;

    /// <summary>The extra port (443) when it could be bound.</summary>
    internal int? ExtraPort => _running?.Extra?.Port;

    internal bool IsRunning => _running is not null;

    internal string NotRunningReason => _running is null ? _notRunning : string.Empty;

    internal GateState Gate => Volatile.Read(ref _gateState);

    internal CertificateSlot Certificates => _slot;

    internal LoginThrottle Throttle => _throttle;

    /// <summary>The start that ApplicationStarted kicked off, for tests to wait on.</summary>
    internal Task StartTask => _startTask ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Register again: safe to repeat, and the capability below must be stamped with this host's generation.
            CampgroundLifetime.Register(PluginIds.Trailhead, _lifetime);
            HttpsClient.Provide(PluginIds.Trailhead, HttpsClient.StatusMinor, ProvisionAsync, StatusAsync);
            _lifetime.ApplicationStarted.Register(() => _startTask = Task.Run(() => EnsureStartedAsync(CancellationToken.None)));
            _lifetime.ApplicationStopping.Register(() => _stopTask = StopEdgeAsync());
            if (_certificates is not null)
            {
                _certificates.CertificateIssued += OnCertificateIssued;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's listener couldn't hook into Jellyfin's start ({Error}); Jellyfin carries on without it", e.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await (_stopTask ?? StopEdgeAsync()).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's listener didn't stop cleanly ({Error})", e.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (_certificates is not null)
        {
            _certificates.CertificateIssued -= OnCertificateIssued;
        }

        _stopping.Cancel();
    }

    /// <summary>
    /// Starts Edge if setup has what it needs and it isn't running; if it is, takes up a new certificate. Never throws.
    /// Setup's certificate step and a renewal both end here.
    /// </summary>
    internal async Task EnsureStartedAsync(CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await StartCoreAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _notRunning = EdgeNotRunning.Stopping;
        }
        catch (Exception e)
        {
            _notRunning = EdgeNotRunning.StartFailed;
            _logger.LogWarning("Trailhead's listener couldn't start ({Error}); Jellyfin carries on without it", e.GetType().Name);
        }
    }

    /// <summary>Checks the gate now and returns what it found (tests, and the health task later).</summary>
    internal async Task<GateState> CheckGateAsync(CancellationToken ct)
    {
        var running = _running;
        if (running is null)
        {
            return Gate;
        }

        var state = await EvaluateGateAsync(running, ct).ConfigureAwait(false);
        Publish(state);
        return state;
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (_running is not null)
        {
            ReloadCertificate();
            return;
        }

        var env = _environment();
        var config = env.Config();
        if (string.IsNullOrWhiteSpace(config.Hostname))
        {
            // Do no harm (a): not set up means nothing bound and nothing read or made.
            _notRunning = EdgeNotRunning.NotSetUp;
            return;
        }

        if (!env.HasCertificate() || env.Certificate() is not { } stored)
        {
            _notRunning = EdgeNotRunning.NoCertificate;
            return;
        }

        if (env.Forwarding() is not { } forwarding)
        {
            _notRunning = EdgeNotRunning.NoForwardingAddress;
            _logger.LogWarning("Trailhead's listener didn't start: it couldn't tell which address Jellyfin listens on");
            return;
        }

        var jellyfinPort = env.JellyfinPort();
        if (jellyfinPort is < 1 or > 65535)
        {
            _notRunning = EdgeNotRunning.NoJellyfinPort;
            return;
        }

        _slot.Swap(ServingCertificate.From(stored));
        var basePath = Campground.Shared.Network.JellyfinNetworkSettings.NormalizeBaseUrl(env.BaseUrl());
        var settings = new ProxySettings(forwarding, jellyfinPort, basePath, _options.ActivityTimeout, _options.RetryAfter);
        var edgeLoggers = new EdgeLoggerFactory(_loggers);
        var proxy = new EdgeProxy(settings, _identity, () => Gate, _throttle, edgeLoggers, _logger);
        Publish(new GateState(false, GateReasons.Checking, _time.GetUtcNow()));

        EdgeListener? main = null;
        EdgeListener? extra = null;
        try
        {
            var jellyfinPorts = new HashSet<int> { jellyfinPort };
            if (env.JellyfinHttpsPort() is { } https)
            {
                jellyfinPorts.Add(https);
            }

            main = await BindMainAsync(proxy, edgeLoggers, jellyfinPorts, ct).ConfigureAwait(false);
            if (main is null)
            {
                _notRunning = EdgeNotRunning.NoFreePort;
                _logger.LogWarning("Trailhead's listener didn't start: none of its ports ({Ports}) is free", string.Join(", ", _options.Ports));
                await proxy.DisposeAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                _ports.ReserveEdge(main.Port);
            }
            catch (Exception e)
            {
                // No record, no change: a listener on a port the table doesn't hold would be a second source of truth.
                _notRunning = EdgeNotRunning.JournalUnavailable;
                _logger.LogWarning("Trailhead's listener didn't start: its port couldn't be recorded ({Error})", e.GetType().Name);
                await main.DisposeAsync().ConfigureAwait(false);
                await proxy.DisposeAsync().ConfigureAwait(false);
                return;
            }

            if (_options.ExtraPort is { } port443 && port443 != main.Port && !jellyfinPorts.Contains(port443))
            {
                extra = await EdgeListener.TryStartAsync(_options.ListenAddress, port443, proxy.HandleAsync, _slot, edgeLoggers, ct).ConfigureAwait(false);
                if (extra is null && !_extraLogged)
                {
                    // Normal on Linux without the capability, and wherever something else has 443 (T36).
                    _extraLogged = true;
                    _logger.LogInformation("Trailhead's listener couldn't use port {Port} here, so it listens on {Main} only", port443, main.Port);
                }
            }
        }
        catch
        {
            if (extra is not null)
            {
                await extra.DisposeAsync().ConfigureAwait(false);
            }

            if (main is not null)
            {
                await main.DisposeAsync().ConfigureAwait(false);
            }

            await proxy.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var source = SelfTest.SourceFor(forwarding);
        var mainPort = main.Port;
        var selfTest = new SelfTest(() => SelfTest.LoopbackOf(_options.ListenAddress, mainPort), source, _identity.ProbeToken, basePath, () => _slot.Current?.Thumbprint, env.Version(), _options.SelfTestTimeout);
        var loop = new CancellationTokenSource();
        var running = new Running(main, extra, proxy, forwarding, selfTest, env, loop);
        _identity.Started(forwarding);
        _running = running;
        _notRunning = string.Empty;
        _logger.LogInformation("Trailhead's listener is on port {Port}{Extra}; it opens to outside connections once its checks pass", main.Port, extra is null ? string.Empty : " and " + extra.Port);
        running.Loop = Task.Run(() => GateLoopAsync(running, loop.Token));
    }

    // The reserved port first, retried for a few seconds (the old copy, or another program, may be letting go), then
    // the rest of the list once each. The table only moves when the reserved port stays taken.
    private async Task<EdgeListener?> BindMainAsync(EdgeProxy proxy, ILoggerFactory edgeLoggers, IReadOnlySet<int> jellyfinPorts, CancellationToken ct)
    {
        var reserved = _ports.EdgePort();
        var candidates = (reserved is { } r ? new[] { r } : Array.Empty<int>()).Concat(_options.Ports).Distinct().Where(p => !jellyfinPorts.Contains(p)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var first = candidates[0];
        var until = _time.GetUtcNow() + _options.BindRetry;
        while (true)
        {
            var listener = await EdgeListener.TryStartAsync(_options.ListenAddress, first, proxy.HandleAsync, _slot, edgeLoggers, ct).ConfigureAwait(false);
            if (listener is not null)
            {
                return listener;
            }

            if (_time.GetUtcNow() >= until)
            {
                break;
            }

            await Task.Delay(_options.BindRetryStep, _time, ct).ConfigureAwait(false);
        }

        foreach (var port in candidates.Skip(1))
        {
            var listener = await EdgeListener.TryStartAsync(_options.ListenAddress, port, proxy.HandleAsync, _slot, edgeLoggers, ct).ConfigureAwait(false);
            if (listener is not null)
            {
                _logger.LogWarning("Port {Wanted} is in use, so Trailhead's listener moved to port {Port}", first, port);
                return listener;
            }
        }

        return null;
    }

    private async Task GateLoopAsync(Running running, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Publish(await EvaluateGateAsync(running, ct).ConfigureAwait(false));
                await Task.Delay(Gate.IsOpen ? _options.RecheckOpen : _options.RecheckClosed, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Publish(new GateState(false, GateReasons.SelfTestFailed, _time.GetUtcNow()));
                _logger.LogWarning("Trailhead's listener couldn't check itself ({Error})", e.GetType().Name);
                try
                {
                    await Task.Delay(_options.RecheckClosed, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task<GateState> EvaluateGateAsync(Running running, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        IPAddress? forwardingNow;
        ForwardedHeadersOptions? live;
        try
        {
            forwardingNow = running.Environment.Forwarding();
            live = running.Environment.Live();
        }
        catch (Exception)
        {
            return new GateState(false, GateReasons.NotTrusted, now);
        }

        if (forwardingNow is null || !forwardingNow.Equals(running.Forwarding))
        {
            return new GateState(false, GateReasons.ForwardingChanged, now);
        }

        if (!LiveTrust.Trusts(live, running.Forwarding))
        {
            return new GateState(false, GateReasons.NotTrusted, now);
        }

        var result = await running.SelfTest.RunAsync(ct).ConfigureAwait(false);
        return new GateState(result.Passed, result.Reason, _time.GetUtcNow());
    }

    private void Publish(GateState state)
    {
        var before = Interlocked.Exchange(ref _gateState, state);
        if (before.IsOpen == state.IsOpen && (state.IsOpen || before.Reason == state.Reason))
        {
            return;
        }

        // Shut from the first check, or shut for a new reason: logged once each, or an admin whose listener never
        // opens has nothing to go on (the first ts1 run sat on 503 with an empty log).
        if (!state.IsOpen && !before.IsOpen)
        {
            _logger.LogWarning("Trailhead's listener is still closed to outside connections: {Reason}", state.Reason);
            return;
        }

        if (state.IsOpen)
        {
            _logger.LogInformation("Trailhead's listener passed its checks and is open to outside connections");
        }
        else
        {
            _logger.LogWarning("Trailhead's listener closed to outside connections: {Reason}", state.Reason);
        }
    }

    private void ReloadCertificate()
    {
        try
        {
            if (_environment().Certificate() is { } stored && _slot.Swap(ServingCertificate.From(stored)))
            {
                _logger.LogInformation("Trailhead's listener is serving its new certificate");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's listener couldn't load the new certificate ({Error}); it keeps the one it has", e.GetType().Name);
        }
    }

    private void OnCertificateIssued() => _ = Task.Run(() => EnsureStartedAsync(CancellationToken.None));

    private async Task StopEdgeAsync()
    {
        _stopping.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var running = _running;
            _running = null;
            _notRunning = EdgeNotRunning.Stopping;
            _identity.Stopped();

            // Not Publish: a stop isn't the gate closing on drift, and would log a warning at every restart.
            Volatile.Write(ref _gateState, new GateState(false, GateReasons.Checking, _time.GetUtcNow()));
            if (running is not null)
            {
                await running.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's listener didn't stop cleanly ({Error})", e.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<HttpsProvisionResult> ProvisionAsync(HttpsProvisionRequest request, CancellationToken ct)
    {
        // Setup opens a port on the router, so it is never run unattended (03 §6): the admin is sent to the page.
        string basePath;
        try
        {
            basePath = Campground.Shared.Network.JellyfinNetworkSettings.NormalizeBaseUrl(_environment().BaseUrl());
        }
        catch (Exception)
        {
            basePath = string.Empty;
        }

        return Task.FromResult(new HttpsProvisionResult(false, UiText.English("edge.provision"), basePath + "/web/#/configurationpage?name=Trailhead"));
    }

    /// <summary>https.v1 <c>status</c> (minor 1). The marker is handed to other plugins in this process only.</summary>
    internal Task<HttpsStatus?> StatusAsync(CancellationToken ct)
    {
        try
        {
            var running = _running;
            var env = running?.Environment ?? _environment();
            var config = env.Config();
            var hostname = config.Hostname?.Trim() ?? string.Empty;
            var configured = hostname.Length > 0 && (running is not null || env.HasCertificate());
            string? url = null;
            if (configured && JellyfinNetwork.TryUrl(new NetworkPlan(IPAddress.Loopback, hostname), env.BaseUrl(), out var published, out _))
            {
                url = published;
            }

            var active = running is not null && Gate.IsOpen;
            var forwarding = running?.Forwarding ?? env.Forwarding();
            return Task.FromResult<HttpsStatus?>(new HttpsStatus(configured, url, new EdgeStatus(active, false, forwarding?.ToString(), active ? _identity.Marker : null)));
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't work out its HTTPS status ({Error})", e.GetType().Name);
            return Task.FromResult<HttpsStatus?>(null);
        }
    }

    private static EdgeEnvironment FromJellyfin(IServiceProvider services, CertificateServices certificates)
    {
        var host = services.GetRequiredService<IServerApplicationHost>();
        var config = services.GetRequiredService<IConfigurationManager>();
        var network = services.GetRequiredService<INetworkManager>();
        var live = services.GetService<IOptions<ForwardedHeadersOptions>>();
        return new EdgeEnvironment
        {
            Config = ConfigGate.Read,
            JellyfinPort = () => host.HttpPort,
            JellyfinHttpsPort = () => host.ListenWithHttps ? host.HttpsPort : null,
            BaseUrl = () => config.GetNetworkConfiguration().BaseUrl,
            Forwarding = () => ForwardingAddress.Current(network, config),
            Live = () => live?.Value,
            Version = () => host.ApplicationVersionString,
            Certificate = certificates.LoadServing,
            HasCertificate = () => CertificateStore.HasCurrent(certificates.CertificateFolder()),
        };
    }

    private sealed class Running : IAsyncDisposable
    {
        private readonly CancellationTokenSource _loop;

        internal Running(EdgeListener main, EdgeListener? extra, EdgeProxy proxy, IPAddress forwarding, SelfTest selfTest, EdgeEnvironment environment, CancellationTokenSource loop)
        {
            Main = main;
            Extra = extra;
            Proxy = proxy;
            Forwarding = forwarding;
            SelfTest = selfTest;
            Environment = environment;
            _loop = loop;
        }

        internal EdgeListener Main { get; }

        internal EdgeListener? Extra { get; }

        internal EdgeProxy Proxy { get; }

        internal IPAddress Forwarding { get; }

        internal SelfTest SelfTest { get; }

        internal EdgeEnvironment Environment { get; }

        internal Task? Loop { get; set; }

        public async ValueTask DisposeAsync()
        {
            _loop.Cancel();
            if (Loop is { } loop)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (Extra is not null)
            {
                await Extra.DisposeAsync().ConfigureAwait(false);
            }

            await Main.DisposeAsync().ConfigureAwait(false);
            await Proxy.DisposeAsync().ConfigureAwait(false);
            _loop.Dispose();
        }
    }
}

using Campground.Shared.Registry;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Network;
using Jellyfin.Plugin.Trailhead.Router;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>What a test may change about how the router side reaches the world. A user can't set any of it.</summary>
internal sealed class RouterOverrides
{
    internal string? DataFolder { get; init; }

    internal IRouterFinder? Finder { get; init; }

    internal INetworkView? View { get; init; }

    internal IFileProbe? Files { get; init; }

    internal IPublicAddressLookup? PublicLookup { get; init; }

    /// <summary>The handler under the router's HTTP client (description and SOAP).</summary>
    internal HttpMessageHandler? RouterHandler { get; init; }

    internal TimeProvider? Time { get; init; }

    internal SemaphoreSlim? RouterLock { get; init; }

    internal IProcessRunner? Runner { get; init; }

    internal Func<FirewallEnvironment>? Firewall { get; init; }

    internal TimeSpan? SearchWindow { get; init; }
}

/// <summary>The last lease run and when it finished. In memory only.</summary>
internal sealed record LastLeaseRun(IReadOnlyList<ForwardReport> Forwards, DateTimeOffset At);

/// <summary>
/// The router side for this Jellyfin's lifetime: the probe, the mapper over the recorded-forwards list, and the host
/// firewall. Built on first use, never at construction, so a broken network can't stop Jellyfin starting.
/// </summary>
public sealed class RouterServices : IDisposable
{
    private static readonly TimeSpan DefaultSearchWindow = TimeSpan.FromSeconds(3);

    private readonly ILogger _logger;
    private readonly RouterOverrides _overrides;
    private readonly IApplicationPaths? _paths;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private HttpClient? _http;
    private PublicAddressLookup? _ownLookup;
    private RouterMapper? _mapper;
    private NetworkProbe? _probe;
    private HostFirewall? _firewall;
    private LastLeaseRun? _last;
    private bool _disposed;

    public RouterServices(IApplicationPaths applicationPaths, ILogger<RouterServices> logger)
        : this(logger, new RouterOverrides(), applicationPaths)
    {
    }

    internal RouterServices(ILogger logger, RouterOverrides overrides, IApplicationPaths? paths = null)
    {
        _logger = logger;
        _overrides = overrides;
        _paths = paths;
    }

    /// <summary>Whether anything has been built yet. For tests: construction must do no work.</summary>
    internal bool Built
    {
        get
        {
            lock (_gate)
            {
                return _http is not null;
            }
        }
    }

    internal RouterMapper Mapper
    {
        get
        {
            lock (_gate)
            {
                Ready();
                return _mapper ??= new RouterMapper(Finder(), LanFor, Client, new ForwardsStore(Path.Combine(Folder(), "forwards.json")), Time, RouterLock(), _logger);
            }
        }
    }

    internal NetworkProbe Probe
    {
        get
        {
            lock (_gate)
            {
                Ready();
                return _probe ??= new NetworkProbe(Finder(), _overrides.PublicLookup ?? (_ownLookup ??= new PublicAddressLookup()), View, Files, Client, _logger);
            }
        }
    }

    internal HostFirewall Firewall
    {
        get
        {
            lock (_gate)
            {
                Ready();
                return _firewall ??= new HostFirewall(
                    Path.Combine(Folder(), "firewall.json"),
                    _overrides.Runner ?? SystemProcessRunner.Instance,
                    _overrides.Firewall ?? (() => HostFirewall.Detect(Files, View.Interfaces())),
                    Time,
                    _logger);
            }
        }
    }

    internal LastLeaseRun? LastRun => Volatile.Read(ref _last);

    /// <summary>The lease task's work. Never throws except <see cref="OperationCanceledException"/>.</summary>
    internal async Task<IReadOnlyList<ForwardReport>> RenewAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        var reports = await Mapper.RenewAsync(linked.Token).ConfigureAwait(false);
        Volatile.Write(ref _last, new LastLeaseRun(reports, Time.GetUtcNow()));
        return reports;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping.Cancel();
            _http?.Dispose();
            _ownLookup?.Dispose();
        }
    }

    private TimeProvider Time => _overrides.Time ?? TimeProvider.System;

    private INetworkView View => _overrides.View ?? SystemNetworkView.Instance;

    private IFileProbe Files => _overrides.Files ?? SystemFileProbe.Instance;

    private void Ready()
    {
        if (_disposed)
        {
            throw new OperationCanceledException();
        }

        _http ??= new HttpClient(_overrides.RouterHandler ?? new SocketsHttpHandler
        {
            // The router is on the LAN: a system proxy would send the SOAP calls to the internet instead.
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private IRouterFinder Finder()
        => _overrides.Finder ?? new RouterFinder(_http!, () => RouterFinder.SystemPlans(View), _overrides.SearchWindow ?? DefaultSearchWindow);

    private IgdClient Client(IgdService service) => new(_http!, service);

    private System.Net.IPAddress? LanFor(System.Net.IPAddress router) => LanAddressPicker.Pick(router, View.Interfaces());

    private string Folder()
    {
        if (_overrides.DataFolder is { } folder)
        {
            return folder;
        }

        if (_paths is not null)
        {
            CampgroundPaths.Initialize(_paths);
        }

        return CampgroundPaths.ForPlugin("trailhead");
    }

    // One lock for the router in the whole process, found through the Campground registry. Jellyfin 12 restarts in
    // the same process, and a lock held in this object would let an old copy's task and the new copy's wizard both
    // reach the router at once (T26; FieldGuide, #17).
    private SemaphoreSlim RouterLock()
        => _overrides.RouterLock ?? (SemaphoreSlim)CampgroundRegistry.Section("trailhead.locks").GetOrAdd("router", _ => new SemaphoreSlim(1, 1));
}

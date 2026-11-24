using Campground.Shared.Registry;
using Campground.Shared.Security;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// The address sync, the outside check and the health run for this Jellyfin's lifetime (M4). Built on first use, never
/// at construction: installing Trailhead reads, makes and sends nothing (do no harm), and a Jellyfin service that
/// isn't there fails the one part that needs it, when it is first asked for.
/// </summary>
public sealed class HealthServices : IDisposable
{
    private readonly IServiceProvider? _services;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Func<AddressSync>? _syncFactory;
    private readonly Func<HealthService>? _healthFactory;
    private AddressSync? _sync;
    private HealthService? _health;
    private CheckHostClient? _checkHost;
    private PhoneTest? _phone;
    private bool _disposed;

    public HealthServices(IServiceProvider services, ILogger<HealthServices> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>For tests: the parts made elsewhere.</summary>
    internal HealthServices(ILogger logger, Func<AddressSync> sync, Func<HealthService> health, PhoneTest phone)
    {
        _logger = logger;
        _syncFactory = sync;
        _healthFactory = health;
        _phone = phone;
    }

    /// <summary>Whether anything has been built yet. For tests: construction must do no work.</summary>
    internal bool Built
    {
        get
        {
            lock (_gate)
            {
                return _sync is not null || _health is not null;
            }
        }
    }

    /// <summary>The phone test Edge answers. In memory and empty until a link is made.</summary>
    internal PhoneTest Phone
    {
        get
        {
            lock (_gate)
            {
                return _phone ??= _services!.GetRequiredService<PhoneTest>();
            }
        }
    }

    internal AddressSync Sync
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _sync ??= _syncFactory?.Invoke() ?? BuildSync();
            }
        }
    }

    internal HealthService Health
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _health ??= _healthFactory?.Invoke() ?? BuildHealth();
            }
        }
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
            _health?.Dispose();
            _checkHost?.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new OperationCanceledException();
        }
    }

    private string Folder()
    {
        CampgroundPaths.Initialize(_services!.GetRequiredService<IApplicationPaths>());
        return CampgroundPaths.ForPlugin("trailhead");
    }

    private AddressSync BuildSync()
    {
        var sp = _services!;
        var router = sp.GetRequiredService<RouterServices>();
        var watch = sp.GetRequiredService<PublicAddressWatch>();
        var folder = Folder();
        return new AddressSync(
            ConfigGate.Read,
            sealedKey => new DynuKey(SecretBox.ForPurpose("trailhead")).TryOpen(sealedKey, out var key) ? key : null,
            key => DynuClient.Create(key, _logger, TimeProvider.System),
            router.PublicLookup,
            Path.Combine(folder, "address.json"),
            watch,

            // One DNS lock in the process (T26), found through the registry like the router's, so an old copy left by an
            // in-process restart and the new one never update the record at once.
            (SemaphoreSlim)CampgroundRegistry.Section("trailhead.locks").GetOrAdd("dns", _ => new SemaphoreSlim(1, 1)),
            TimeProvider.System,
            _logger);
    }

    private HealthService BuildHealth()
    {
        var sp = _services!;
        var certificates = sp.GetRequiredService<CertificateServices>();
        var router = sp.GetRequiredService<RouterServices>();
        var sync = Sync;
        _checkHost = new CheckHostClient(null, _logger);
        var outside = new OutsideCheckSwitch(_checkHost, () => ConfigGate.Read().OutsideCheckAllowed);
        return new HealthService(
            new HealthEnvironment
            {
                Config = ConfigGate.Read,
                HasCertificate = () => CertificateStore.HasCurrent(certificates.CertificateFolder()),
                Edge = () => sp.GetService<EdgeService>()?.LoopbackEndPoint,
                Gate = () => sp.GetService<EdgeService>() is { IsRunning: true } e ? e.Gate : null,
                StoredThumbprint = certificates.StoredThumbprint,
                Drift = () => sp.GetRequiredService<JellyfinNetwork>().CheckDrift(),
                Forwards = router.RecordedForwards,
                Leases = () => router.LastRun,
                Sync = () => sync.Last,
                Outside = outside,
                Phone = sp.GetService<PhoneTest>(),
                Watch = sp.GetService<PublicAddressWatch>(),
                Alerts = new ActivityLogAlerts(() => sp.GetService<IActivityManager>()),
                Folder = Folder,
            },
            _logger);
    }
}

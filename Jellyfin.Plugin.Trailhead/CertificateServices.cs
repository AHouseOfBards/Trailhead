using System.Net;
using System.Security.Cryptography.X509Certificates;
using Campground.Shared.Security;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>The two saved settings a renewal needs, copied out of the configuration.</summary>
internal sealed record ServiceSettings(string Hostname, string SealedDynuKey);

internal enum RenewalState
{
    /// <summary>Nothing to renew yet: no address, no key, or a key that won't open. A normal state.</summary>
    NotSetUp,

    /// <summary>The manager ran; <see cref="RenewalReport.Result"/> says how it went.</summary>
    Ran,

    /// <summary>Making the manager threw. Logged, and the next run tries again.</summary>
    BuildFailed,
}

internal sealed record RenewalReport(RenewalState State, string Message, ReconcileResult? Result = null);

/// <summary>
/// Everything a test may change about how the services reach the outside. A user can't set any of it: the only way
/// in is the internal constructor, and the public one passes none, so the endpoint is Let's Encrypt staging.
/// </summary>
internal sealed class ServicesOverrides
{
    internal SecretBox? Box { get; init; }

    /// <summary>Where the account key, certificates and failure budget go. Default: Trailhead's folder under Campground's.</summary>
    internal string? DataFolder { get; init; }

    internal TimeProvider? Time { get; init; }

    internal Uri? AcmeDirectory { get; init; }

    /// <summary>Trusted for the ACME connection only (Pebble's CA).</summary>
    internal X509Certificate2? ExtraCa { get; init; }

    internal Uri? DynuBase { get; init; }

    internal IReadOnlyList<IPEndPoint>? DnsServers { get; init; }

    internal DnsWaitOptions? DnsWait { get; init; }

    internal IssuerOptions? Issuer { get; init; }

    internal string? Profile { get; init; }

    internal string? ExpectedRoot { get; init; }

    internal string? PreferredRoot { get; init; }

    /// <summary>Replaces the real issuer (and so the ACME and Dynu clients, which are then not made).</summary>
    internal Func<ICertificateIssuer>? IssuerFactory { get; init; }

    /// <summary>Replaces the whole manager.</summary>
    internal Func<ICertificateRenewer>? RenewerFactory { get; init; }
}

/// <summary>
/// Owns the one <see cref="CertificateManager"/> for this Jellyfin's lifetime. It is built on the first renewal, not
/// at construction, from the settings saved at that moment, and rebuilt when they change. A server that isn't set up
/// is a normal state here, not an error.
/// </summary>
public sealed class CertificateServices : IDisposable
{
    // Staging stand-ins for Let's Encrypt's classic profile and the root old TVs trust (spike T-S2). Production is
    // not reachable from here on purpose; nothing in this class asks AcmeEndpoints for it.
    private const string StagingProfile = "classic";
    private const string StagingRoot = "(STAGING) Pretend Pear X1";
    private static readonly TimeSpan AcmeTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly Func<ServiceSettings> _read;
    private readonly ServicesOverrides _overrides;
    private readonly IApplicationPaths? _paths;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Built? _built;
    private bool _disposed;

    public CertificateServices(IApplicationPaths applicationPaths, ILogger<CertificateServices> logger)
        : this(logger, ReadPluginSettings, new ServicesOverrides(), applicationPaths)
    {
    }

    internal CertificateServices(ILogger logger, Func<ServiceSettings> read, ServicesOverrides overrides, IApplicationPaths? paths = null)
    {
        _logger = logger;
        _read = read;
        _overrides = overrides;
        _paths = paths;
    }

    /// <summary>How many managers have been built. For tests: same settings must not build another.</summary>
    internal int Builds { get; private set; }

    /// <summary>The CA directory of the manager now in use, or null if none has been built.</summary>
    internal Uri? ActiveDirectory
    {
        get
        {
            lock (_gate)
            {
                return _built?.Directory;
            }
        }
    }

    // Copies strings out and keeps nothing: Jellyfin swaps the configuration object whenever an admin saves (advice 1).
    private static ServiceSettings ReadPluginSettings()
    {
        var config = Plugin.Instance?.Configuration;
        return new ServiceSettings(config?.Hostname ?? string.Empty, config?.DynuKey ?? string.Empty);
    }

    /// <summary>
    /// One renewal check. A caller that arrives while another is running shares its result (the manager sees to that).
    /// Never throws except <see cref="OperationCanceledException"/>.
    /// </summary>
    internal async Task<RenewalReport> RenewAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        using var lease = Acquire(out var report);
        if (lease is null)
        {
            return report!;
        }

        var result = await lease.Renewer.ReconcileAsync(false, linked.Token).ConfigureAwait(false);
        return new RenewalReport(RenewalState.Ran, string.Empty, result);
    }

    /// <summary>The manager for the settings saved now, built if need be. Null (with a report) when there is nothing to renew.</summary>
    internal Lease? Acquire(out RenewalReport? report)
    {
        report = null;
        lock (_gate)
        {
            if (_disposed)
            {
                // Jellyfin is stopping: the same as being cancelled, which is the one thing a run may throw.
                throw new OperationCanceledException();
            }

            ServiceSettings settings;
            try
            {
                settings = _read();
            }
            catch (Exception e)
            {
                report = new RenewalReport(RenewalState.BuildFailed, "Trailhead could not read its settings (" + e.GetType().Name + ").");
                return null;
            }

            if (_built is { } current && current.Settings == settings)
            {
                return current.Lease();
            }

            // Settings changed. The old manager may be mid-run for the old name; it finishes, then goes.
            _built?.Retire();
            _built = null;

            if (!TryName(settings.Hostname, out var name))
            {
                report = new RenewalReport(RenewalState.NotSetUp, string.IsNullOrWhiteSpace(settings.Hostname)
                    ? "Trailhead has no web address saved yet, so there is no certificate to renew."
                    : "The saved web address isn't a valid host name, so there is no certificate to renew.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(settings.SealedDynuKey))
            {
                report = new RenewalReport(RenewalState.NotSetUp, "Trailhead has no Dynu key saved yet, so there is no certificate to renew.");
                return null;
            }

            var owned = new List<IDisposable>();
            try
            {
                var box = _overrides.Box ?? OpenBox();
                if (!new DynuKey(box).TryOpen(settings.SealedDynuKey, out var plainKey))
                {
                    report = new RenewalReport(RenewalState.NotSetUp, "The saved Dynu key can't be opened here. " + DynuKey.ReenterMessage);
                    return null;
                }

                var built = Build(settings, name, box, plainKey!, owned);
                Builds++;
                _built = built;
                return built.Lease();
            }
            catch (Exception e)
            {
                foreach (var d in owned)
                {
                    d.Dispose();
                }

                // The type only, never the text: it can carry a path, a URL or worse.
                report = new RenewalReport(RenewalState.BuildFailed, "Trailhead could not get ready to renew (" + e.GetType().Name + ").");
                return null;
            }
        }
    }

    public void Dispose()
    {
        Built? built;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            built = _built;
            _built = null;
        }

        // Cancels a run in flight; its lease releases the clients when it lets go. The source is left undisposed:
        // a late RenewAsync still reads its token, and it holds no timer.
        _stopping.Cancel();
        built?.Retire();
    }

    private static bool TryName(string hostname, out string name)
    {
        name = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        return name.Length > 0 && Uri.CheckHostName(name) == UriHostNameType.Dns;
    }

    private SecretBox OpenBox()
    {
        if (_paths is not null)
        {
            CampgroundPaths.Initialize(_paths);
        }

        return SecretBox.ForPurpose("trailhead");
    }

    private Built Build(ServiceSettings settings, string name, SecretBox box, string plainKey, List<IDisposable> owned)
    {
        var time = _overrides.Time ?? TimeProvider.System;

        // Staging has its own folder, so a staging certificate can never be taken for a production one later.
        var folder = _overrides.DataFolder ?? Path.Combine(CampgroundPaths.ForPlugin("trailhead"), "staging");
        var store = new CertificateStore(Path.Combine(folder, "certs"), time, _logger);
        var budget = new FailureBudget(Path.Combine(folder, "budget.json"), time);
        var directory = _overrides.AcmeDirectory ?? AcmeEndpoints.Resolve();

        var renewer = _overrides.RenewerFactory?.Invoke() ?? NewManager(name, box, plainKey, folder, directory, store, budget, time, owned);
        return new Built(settings, renewer, directory, owned);
    }

    private CertificateManager NewManager(string name, SecretBox box, string plainKey, string folder, Uri directory, CertificateStore store, FailureBudget budget, TimeProvider time, List<IDisposable> owned)
    {
        ICertificateIssuer issuer;
        if (_overrides.IssuerFactory is { } factory)
        {
            issuer = factory();
        }
        else
        {
            var accountKey = new AccountStore(Path.Combine(folder, "account.key"), box, _logger).LoadOrCreate();
            var dynu = DynuClient.Create(plainKey, _logger, time, _overrides.DynuBase);
            owned.Add(dynu);
            var waiter = AuthoritativeDnsWaiter.Create(_logger, time, _overrides.DnsWait, _overrides.DnsServers);
            var (http, handler) = AcmeHttpHandler.Create(_logger, time, AcmeTimeout, _overrides.ExtraCa);
            owned.Add(http);
            var ctx = CertificateIssuer.NewContext(directory, accountKey, http);
            issuer = new CertificateIssuer(ctx, new DynuChallenge(dynu, waiter, _logger), time, _logger, () => handler.LastRetryAfter, _overrides.Issuer);
        }

        // A test that names its own profile also names its own root, even if that is "none".
        var ownCa = _overrides.Profile is not null;
        var options = new ManagerOptions(name, ownCa ? _overrides.Profile : StagingProfile, ownCa ? _overrides.PreferredRoot : StagingRoot, new CertificateExpectation(name, _overrides.ExpectedRoot ?? StagingRoot));
        return new CertificateManager(store, issuer, budget, time, _logger, options);
    }

    /// <summary>A manager in use. The clients behind it are released when it is retired and the last run has let go.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly Built _owner;

        internal Lease(Built owner) => _owner = owner;

        internal ICertificateRenewer Renewer => _owner.Renewer;

        public void Dispose() => _owner.Release();
    }

    internal sealed class Built
    {
        private readonly List<IDisposable> _owned;
        private readonly object _gate = new();
        private int _leases;
        private bool _retired;
        private bool _released;

        internal Built(ServiceSettings settings, ICertificateRenewer renewer, Uri directory, List<IDisposable> owned)
        {
            Settings = settings;
            Renewer = renewer;
            Directory = directory;
            _owned = owned;
        }

        internal ServiceSettings Settings { get; }

        internal ICertificateRenewer Renewer { get; }

        internal Uri Directory { get; }

        internal Lease Lease()
        {
            lock (_gate)
            {
                _leases++;
            }

            return new Lease(this);
        }

        internal void Release()
        {
            lock (_gate)
            {
                _leases--;
            }

            DisposeIfDone();
        }

        internal void Retire()
        {
            lock (_gate)
            {
                _retired = true;
            }

            DisposeIfDone();
        }

        private void DisposeIfDone()
        {
            lock (_gate)
            {
                if (!_retired || _leases > 0 || _released)
                {
                    return;
                }

                _released = true;
            }

            foreach (var d in _owned)
            {
                d.Dispose();
            }

            _owned.Clear();
        }
    }
}

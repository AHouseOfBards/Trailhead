using System.Globalization;
using System.Net;
using Campground.Shared.Privacy;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.Settings;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>
/// What the health run reads, as functions so a test can stand in for each. Every one may throw: that check then says
/// "couldn't tell", and the others go on.
/// </summary>
internal sealed class HealthEnvironment
{
    internal required Func<PluginConfiguration> Config { get; init; }

    /// <summary>True when setup has a certificate, so Edge should be running.</summary>
    internal required Func<bool> HasCertificate { get; init; }

    /// <summary>Edge's main port on loopback, or null while it isn't running.</summary>
    internal required Func<IPEndPoint?> Edge { get; init; }

    /// <summary>Edge's gate, or null while Edge isn't running.</summary>
    internal required Func<GateState?> Gate { get; init; }

    /// <summary>The thumbprint of the certificate the store holds now.</summary>
    internal required Func<string?> StoredThumbprint { get; init; }

    internal required Func<IReadOnlyList<DriftItem>> Drift { get; init; }

    internal required Func<IReadOnlyList<ForwardRecord>> Forwards { get; init; }

    internal required Func<LastLeaseRun?> Leases { get; init; }

    internal required Func<SyncStatus?> Sync { get; init; }

    internal required IOutsideCheck Outside { get; init; }

    internal PhoneTest? Phone { get; init; }

    internal PublicAddressWatch? Watch { get; init; }

    internal required IAlertSink Alerts { get; init; }

    /// <summary>The folder health.json and alerts.json live in.</summary>
    internal required Func<string> Folder { get; init; }

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    internal TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The key the public address is hashed with. Null: the server's own (shared IpHasher).</summary>
    internal byte[]? HashKey { get; init; }
}

/// <summary>
/// The health run (03 §3 step 10, T20, T23): every part of remote access checked in one pass, saved to
/// <c>health.json</c>, and handed to the escalator. Runs from its scheduled task every ten minutes and from the page.
/// A caller that arrives while a run is going gets that run's result (T26). Never throws except for cancellation.
/// </summary>
internal sealed class HealthService : IDisposable
{
    /// <summary>The gate may close for a moment (a restart, a self-test blip); closed this long is a fault. Not in 03 §3b: chosen here.</summary>
    internal static readonly TimeSpan ListenerGrace = TimeSpan.FromMinutes(15);

    /// <summary>A setting saved but waiting for a restart is normal right after setup; still waiting after this is a fault.</summary>
    internal static readonly TimeSpan RestartGrace = TimeSpan.FromHours(1);

    /// <summary>03 §3b: "outside check 24 h".</summary>
    internal static readonly TimeSpan OutsideGrace = TimeSpan.FromHours(24);

    private readonly HealthEnvironment _env;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task<HealthReport>? _running;
    private HealthReport? _last;

    internal HealthService(HealthEnvironment env, ILogger logger)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>The last report: this process's, or the saved one. Null when there has never been one.</summary>
    internal HealthReport? Last
    {
        get
        {
            var last = Volatile.Read(ref _last);
            if (last is not null)
            {
                return last;
            }

            try
            {
                return File().Load<HealthReport>();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>True when the last run is over an hour old: a stuck or disabled task looks like "all fine" otherwise (03 §3 step 10).</summary>
    internal bool IsStale(HealthReport? report) => report is null || _env.Time.GetUtcNow() - report.CheckedAt > TimeSpan.FromHours(1);

    internal Task<HealthReport> RunAsync(CancellationToken ct)
    {
        Task<HealthReport> run;
        lock (_gate)
        {
            if (_running is null || _running.IsCompleted)
            {
                _running = Task.Run(() => RunCoreAsync(_stopping.Token));
            }

            run = _running;
        }

        return run.WaitAsync(ct);
    }

    public void Dispose() => _stopping.Cancel();

    /// <summary>The saved name, or empty.</summary>
    internal string Hostname() => Try(() => _env.Config(), "settings")?.Hostname?.Trim().TrimEnd('.') ?? string.Empty;

    /// <summary>The port the outside reaches: Trailhead's recorded forward's, else 443 (T16).</summary>
    internal int ExternalPort()
        => TryGet(() => _env.Forwards(), out var forwards) && forwards?.FirstOrDefault(f => f.Owner == RouterMapper.TrailheadOwner && f.State == ForwardStates.Active) is { } forward
            ? forward.ExternalPort
            : 443;

    private AtomicJsonFile File() => new(Path.Combine(_env.Folder(), "health.json"));

    private async Task<HealthReport> RunCoreAsync(CancellationToken ct)
    {
        var now = _env.Time.GetUtcNow();
        var before = Last;
        var config = Try(() => _env.Config(), "settings") ?? new PluginConfiguration();
        var hostname = config.Hostname?.Trim().TrimEnd('.') ?? string.Empty;

        var checks = new List<HealthCheck>
        {
            await CertificateAsync(hostname, now, ct).ConfigureAwait(false),
            Listener(hostname),
            SettingsCheck(hostname),
            RouterCheck(hostname),
            AddressCheck(hostname, now),
            await OutsideAsync(hostname, ct).ConfigureAwait(false),
        };

        checks = checks.Select(c => Timed(c, before, now)).ToList();
        var hairpin = _env.Watch is { LastSeen: { } seen } w ? new HairpinHint(seen, w.Count) : null;
        var report = new HealthReport(now, Overall.Of(checks), checks, Try(() => _env.Phone?.State, "phone test"), hairpin, Hash(_env.Watch?.Own));
        Volatile.Write(ref _last, report);

        try
        {
            File().Save(report);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't save its health report ({Error})", e.GetType().Name);
        }

        var faults = checks.Where(c => c.Alerting).Select(c => new Fault(c.Id + ":" + c.Code, FaultText(c))).ToList();
        try
        {
            var escalator = new Escalator(Path.Combine(_env.Folder(), "alerts.json"), _env.Alerts, _env.Time, _logger);
            await escalator.ConsiderAsync(faults, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Trailhead couldn't decide on an alert ({Error})", e.GetType().Name);
        }

        return report;
    }

    // Since: kept from the last report while the state and code stay the same, so a grace window survives the runs
    // (and a restart, through health.json). Alerting: a problem that has lasted its check's grace window.
    private static HealthCheck Timed(HealthCheck check, HealthReport? before, DateTimeOffset now)
    {
        var previous = before?.Checks?.FirstOrDefault(c => c.Id == check.Id);
        var since = check.Since ?? (previous is not null && previous.State == check.State && previous.Code == check.Code && previous.Since is { } s ? s : now);
        var alerting = check.State == CheckState.Problem && (check.Alerting || now - since >= GraceFor(check));
        return check with { Since = since, Alerting = alerting };
    }

    private static TimeSpan GraceFor(HealthCheck check) => check.Id switch
    {
        // Not running is the certificate check's listener_down, which alerts at once; one fault, one line.
        CheckIds.Listener when check.Code == "not_running" => TimeSpan.MaxValue,
        CheckIds.Listener => ListenerGrace,
        CheckIds.Settings when check.Code == "saved_not_in_effect" => RestartGrace,
        CheckIds.Outside => OutsideGrace,

        // The router and the address carry their own grace (the mapper's 6 h, the sync's 1 h) in Alerting; the
        // certificate's grace is the 14 days before expiry, already in its code.
        CheckIds.Router or CheckIds.Address => TimeSpan.MaxValue,
        _ => TimeSpan.Zero,
    };

    private async Task<HealthCheck> CertificateAsync(string hostname, DateTimeOffset now, CancellationToken ct)
    {
        if (hostname.Length == 0 || Try(() => _env.HasCertificate(), "certificate") != true)
        {
            return new HealthCheck(CheckIds.Certificate, CheckState.Off, "not_set_up", null, false);
        }

        try
        {
            var facts = await ServedCertificate.CheckAsync(_env.Edge(), hostname, _env.StoredThumbprint(), now, _env.HandshakeTimeout, ct).ConfigureAwait(false);
            var state = facts.Code == CertificateCodes.Ok ? CheckState.Ok : CheckState.Problem;
            var extra = facts.NotAfter is { } until ? new Dictionary<string, string> { ["expires"] = until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } : null;
            return new HealthCheck(CheckIds.Certificate, state, facts.Code, null, false, extra);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't check the certificate it serves ({Error})", e.GetType().Name);
            return new HealthCheck(CheckIds.Certificate, CheckState.CouldNotTell, "check_failed", null, false);
        }
    }

    private HealthCheck Listener(string hostname)
    {
        if (hostname.Length == 0 || Try(() => _env.HasCertificate(), "certificate") != true)
        {
            return new HealthCheck(CheckIds.Listener, CheckState.Off, "not_set_up", null, false);
        }

        if (!TryGet(() => _env.Gate(), out var gate))
        {
            return new HealthCheck(CheckIds.Listener, CheckState.CouldNotTell, "check_failed", null, false);
        }

        if (gate is null)
        {
            // The certificate check reports the listener down at once; this one says the same in gate terms.
            return new HealthCheck(CheckIds.Listener, CheckState.Problem, "not_running", null, false);
        }

        if (gate.IsOpen)
        {
            return new HealthCheck(CheckIds.Listener, CheckState.Ok, "open", null, false);
        }

        return gate.Reason == GateReasons.Checking
            ? new HealthCheck(CheckIds.Listener, CheckState.CouldNotTell, "checking", null, false)
            : new HealthCheck(CheckIds.Listener, CheckState.Problem, "closed", null, false, new Dictionary<string, string> { ["reason"] = gate.Reason });
    }

    private HealthCheck SettingsCheck(string hostname)
    {
        if (!TryGet(() => _env.Drift(), out var drift) || drift is null)
        {
            return hostname.Length == 0
                ? new HealthCheck(CheckIds.Settings, CheckState.Off, "not_set_up", null, false)
                : new HealthCheck(CheckIds.Settings, CheckState.CouldNotTell, "check_failed", null, false);
        }

        if (drift.Count == 0)
        {
            return new HealthCheck(CheckIds.Settings, hostname.Length == 0 ? CheckState.Off : CheckState.CouldNotTell, hostname.Length == 0 ? "not_set_up" : "nothing_recorded", null, false);
        }

        // Only the names of the settings, never their values (Known proxies holds a LAN address).
        Dictionary<string, string> Keys(DriftState s) => new() { ["settings"] = string.Join(",", drift.Where(d => d.State == s).Select(d => d.Key).Distinct().Order(StringComparer.Ordinal)) };
        if (drift.Any(d => d.State == DriftState.Changed))
        {
            return new HealthCheck(CheckIds.Settings, CheckState.Problem, "changed", null, false, Keys(DriftState.Changed));
        }

        if (drift.Any(d => d.State == DriftState.SavedNotInEffect))
        {
            return new HealthCheck(CheckIds.Settings, CheckState.Problem, "saved_not_in_effect", null, false, Keys(DriftState.SavedNotInEffect));
        }

        return drift.Any(d => d.State == DriftState.Unknown)
            ? new HealthCheck(CheckIds.Settings, CheckState.CouldNotTell, "unknown", null, false)
            : new HealthCheck(CheckIds.Settings, CheckState.Ok, "in_effect", null, false);
    }

    private HealthCheck RouterCheck(string hostname)
    {
        if (!TryGet(() => _env.Forwards(), out var forwards) || forwards is null)
        {
            return new HealthCheck(CheckIds.Router, CheckState.CouldNotTell, "check_failed", null, false);
        }

        var ours = forwards.Where(f => f.Owner == RouterMapper.TrailheadOwner).ToList();
        if (hostname.Length == 0 || ours.Count == 0)
        {
            // Not set up, or "finish the router later" (T38): shown on the page, never a fault.
            return new HealthCheck(CheckIds.Router, CheckState.Off, "no_forward", null, false);
        }

        if (!TryGet(() => _env.Leases(), out var run) || run is null)
        {
            return new HealthCheck(CheckIds.Router, CheckState.CouldNotTell, "not_run_yet", null, false);
        }

        var mine = run.Forwards.Where(r => r.Record.Owner == RouterMapper.TrailheadOwner).ToList();
        var bad = mine.FirstOrDefault(r => r.Fault) ?? mine.FirstOrDefault(r => r.Outcome is ForwardOutcome.PointsElsewhere or ForwardOutcome.RouterMissing or ForwardOutcome.Failed);
        if (bad is null)
        {
            return new HealthCheck(CheckIds.Router, CheckState.Ok, "in_place", run.At, false);
        }

        // The mapper's own 6 h grace decides Fault; a blip inside it shows as a problem without an alert.
        var code = bad.Outcome switch
        {
            ForwardOutcome.PointsElsewhere => "points_elsewhere",
            ForwardOutcome.RouterMissing => "router_missing",
            _ => "failed",
        };
        return new HealthCheck(CheckIds.Router, CheckState.Problem, code, null, bad.Fault);
    }

    private HealthCheck AddressCheck(string hostname, DateTimeOffset now)
    {
        if (hostname.Length == 0)
        {
            return new HealthCheck(CheckIds.Address, CheckState.Off, "not_set_up", null, false);
        }

        if (!TryGet(() => _env.Sync(), out var sync))
        {
            return new HealthCheck(CheckIds.Address, CheckState.CouldNotTell, "check_failed", null, false);
        }

        if (sync is null)
        {
            return new HealthCheck(CheckIds.Address, CheckState.CouldNotTell, "not_run_yet", null, false);
        }

        return sync.Outcome switch
        {
            SyncOutcome.NotSetUp => new HealthCheck(CheckIds.Address, CheckState.Off, "not_set_up", null, false),
            SyncOutcome.Unchanged or SyncOutcome.Updated => new HealthCheck(CheckIds.Address, CheckState.Ok, "up_to_date", sync.At, false),
            _ => new HealthCheck(CheckIds.Address, CheckState.Problem, SyncCode(sync.Outcome), sync.FailingSince, sync.IsFault(now)),
        };
    }

    private static string SyncCode(SyncOutcome o) => o switch
    {
        SyncOutcome.NoPublicAddress => "no_public_address",
        SyncOutcome.NameGone => "name_gone",
        SyncOutcome.KeyRejected => "key_rejected",
        _ => "dynu_failed",
    };

    private async Task<HealthCheck> OutsideAsync(string hostname, CancellationToken ct)
    {
        if (hostname.Length == 0)
        {
            return new HealthCheck(CheckIds.Outside, CheckState.Off, "not_set_up", null, false);
        }

        OutsideResult result;
        try
        {
            result = await _env.Outside.CheckAsync(hostname, ExternalPort(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogInformation("Trailhead's outside check failed ({Error})", e.GetType().Name);
            result = new OutsideResult(OutsideState.CouldNotCheck, "checker_failed");
        }

        return result.State switch
        {
            OutsideState.Working => new HealthCheck(CheckIds.Outside, CheckState.Ok, "working", null, false),

            // Better than nothing, and not proof (A7): the page says "the port is open", never "working".
            OutsideState.PortOpen => new HealthCheck(CheckIds.Outside, CheckState.Ok, "port_open", null, false),
            OutsideState.NotReachable => new HealthCheck(CheckIds.Outside, CheckState.Problem, "not_reachable", null, false, new Dictionary<string, string> { ["reason"] = result.Reason ?? string.Empty }),
            OutsideState.NotChecked => new HealthCheck(CheckIds.Outside, CheckState.CouldNotTell, "not_checked", null, false),
            _ => new HealthCheck(CheckIds.Outside, CheckState.CouldNotTell, "could_not_check", null, false),
        };
    }

    private static string FaultText(HealthCheck c)
    {
        var args = new Dictionary<string, object?>();
        if (c.Facts is not null)
        {
            foreach (var (k, v) in c.Facts)
            {
                args[k] = v;
            }
        }

        var key = "alert." + c.Id + "." + c.Code;
        var text = UiText.English(key, args);
        return text == key ? UiText.English("alert.other") : text;
    }

    private string? Hash(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        try
        {
            return _env.HashKey is { } key ? IpHasher.Hash(address, key) : IpHasher.Hash(address);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private T? Try<T>(Func<T?> read, string what)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's health check couldn't read the {What} ({Error})", what, e.GetType().Name);
            return default;
        }
    }

    private bool TryGet<T>(Func<T> read, out T? value)
    {
        try
        {
            value = read();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's health check couldn't read a part of its state ({Error})", e.GetType().Name);
            value = default;
            return false;
        }
    }
}

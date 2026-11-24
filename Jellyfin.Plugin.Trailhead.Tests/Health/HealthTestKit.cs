using System.Net;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

internal sealed record Raised(AlertKind Kind, string Name, string Overview);

/// <summary>Keeps every alert instead of writing to an activity log. Can be told to throw, as a refusing log would.</summary>
internal sealed class RecordingSink : IAlertSink
{
    private readonly List<Raised> _raised = new();

    internal bool Throws { get; set; }

    internal IReadOnlyList<Raised> All
    {
        get
        {
            lock (_raised)
            {
                return _raised.ToList();
            }
        }
    }

    public Task RaiseAsync(AlertKind kind, string name, string overview, CancellationToken ct)
    {
        lock (_raised)
        {
            _raised.Add(new Raised(kind, name, overview));
        }

        return Throws ? throw new InvalidOperationException("the log refused") : Task.CompletedTask;
    }
}

/// <summary>A health run over stand-ins, each settable by a test. Defaults: a name, no certificate, settings in effect, no router forward, the outside check off.</summary>
internal sealed class HealthRig : IDisposable
{
    internal const string Name = "trailhead-test.example.com";

    private readonly TempFolder _folder = new();

    internal HealthRig()
    {
        Service = new HealthService(Env(), Log);
    }

    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

    internal ListLogger Log { get; } = new();

    internal RecordingSink Sink { get; } = new();

    internal PluginConfiguration Config { get; } = new() { Hostname = Name, DynuKey = "sealed" };

    /// <summary>False by default: the certificate and listener checks are then Off, for tests of the other checks.</summary>
    internal bool HasCertificate { get; set; }

    internal IPEndPoint? EdgeAt { get; set; }

    internal GateState? Gate { get; set; } = new(true, GateReasons.Open, DateTimeOffset.UnixEpoch);

    internal string? Stored { get; set; }

    internal List<DriftItem> Drift { get; } = new() { new DriftItem("1", NetworkKeys.KnownProxies, DriftState.InEffect, null) };

    internal List<ForwardRecord> Forwards { get; } = new();

    internal LastLeaseRun? Leases { get; set; }

    internal SyncStatus? Sync { get; set; }

    internal CountingCheck Outside { get; } = new() { Answer = new OutsideResult(OutsideState.NotChecked, "switched_off") };

    internal PhoneTest Phone { get; } = new();

    internal PublicAddressWatch Watch { get; } = new();

    internal HealthService Service { get; private set; }

    internal string Folder => _folder.Path;

    internal void Restart() => Service = new HealthService(Env(), Log);

    internal async Task<HealthReport> RunAsync() => await Service.RunAsync(CancellationToken.None);

    internal HealthCheck Check(HealthReport report, string id) => report.Checks.Single(c => c.Id == id);

    internal void Advance(TimeSpan by) => Time.Advance(by);

    public void Dispose()
    {
        Service.Dispose();
        _folder.Dispose();
    }

    private HealthEnvironment Env() => new()
    {
        Config = () => Config,
        HasCertificate = () => HasCertificate,
        Edge = () => EdgeAt,
        Gate = () => Gate,
        StoredThumbprint = () => Stored,
        Drift = () => Drift,
        Forwards = () => Forwards,
        Leases = () => Leases,
        Sync = () => Sync,
        Outside = Outside,
        Phone = Phone,
        Watch = Watch,
        Alerts = Sink,
        Folder = () => Folder,
        Time = Time,
        HashKey = new byte[32],
        HandshakeTimeout = TimeSpan.FromSeconds(5),
    };
}

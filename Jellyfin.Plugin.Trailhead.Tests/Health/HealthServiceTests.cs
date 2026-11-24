using System.Net;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Edge;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

/// <summary>The health run over stand-ins: grace windows, "couldn't tell", the report file and the alerts it leads to.</summary>
public sealed class HealthServiceTests : IDisposable
{
    private readonly HealthRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static ForwardRecord Forward(int external = 443) => new("f1", RouterMapper.TrailheadOwner, "TCP", external, 8443, "192.168.1.10", ForwardStates.Active, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Not_set_up_every_check_is_off_and_nothing_is_reported()
    {
        _rig.Config.Hostname = string.Empty;
        _rig.Drift.Clear();

        var report = await _rig.RunAsync();

        Assert.Equal(Overall.NotSetUp, report.Overall);
        Assert.All(report.Checks, c => Assert.Equal(CheckState.Off, c.State));
        Assert.Empty(_rig.Sink.All);
    }

    [Fact]
    public async Task With_the_outside_check_off_the_answer_is_unknown_never_ok()
    {
        _rig.Sync = new SyncStatus(SyncOutcome.Unchanged, _rig.Time.GetUtcNow(), null, false);

        var report = await _rig.RunAsync();

        Assert.Equal(CheckState.CouldNotTell, _rig.Check(report, CheckIds.Outside).State);
        Assert.Equal(Overall.Unknown, report.Overall);
    }

    [Fact]
    public async Task Couldnt_tell_is_its_own_state_for_every_part_that_throws()
    {
        var env = new HealthEnvironment
        {
            Config = () => _rig.Config,
            HasCertificate = () => true,
            Edge = () => throw new InvalidOperationException(),
            Gate = () => throw new InvalidOperationException(),
            StoredThumbprint = () => throw new InvalidOperationException(),
            Drift = () => throw new InvalidOperationException(),
            Forwards = () => throw new InvalidOperationException(),
            Leases = () => throw new InvalidOperationException(),
            Sync = () => throw new InvalidOperationException(),
            Outside = new OutsideCheckSwitch(new CountingCheck(), () => throw new InvalidOperationException()),
            Alerts = _rig.Sink,
            Folder = () => _rig.Folder,
            Time = _rig.Time,
            HashKey = new byte[32],
        };
        using var service = new HealthService(env, _rig.Log);

        var report = await service.RunAsync(CancellationToken.None);

        Assert.All(report.Checks, c => Assert.Equal(CheckState.CouldNotTell, c.State));
        Assert.Equal(Overall.Unknown, report.Overall);
        Assert.Empty(_rig.Sink.All);
    }

    [Fact]
    public async Task A_changed_setting_alerts_at_once_and_names_the_setting_not_its_value()
    {
        _rig.Drift[0] = new DriftItem("1", NetworkKeys.KnownProxies, DriftState.Changed, System.Text.Json.JsonDocument.Parse("[\"192.168.1.10\"]").RootElement);

        var report = await _rig.RunAsync();

        var check = _rig.Check(report, CheckIds.Settings);
        Assert.Equal(CheckState.Problem, check.State);
        Assert.True(check.Alerting);
        var alert = Assert.Single(_rig.Sink.All);
        Assert.Equal(AlertKind.Started, alert.Kind);
        Assert.Contains(NetworkKeys.KnownProxies, alert.Overview, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168", alert.Overview, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168", File.ReadAllText(Path.Combine(_rig.Folder, "health.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_setting_waiting_for_a_restart_is_a_fault_only_after_an_hour()
    {
        _rig.Drift[0] = new DriftItem("1", NetworkKeys.KnownProxies, DriftState.SavedNotInEffect, null);

        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Settings).Alerting);
        _rig.Advance(TimeSpan.FromMinutes(59));
        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Settings).Alerting);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.True(_rig.Check(await _rig.RunAsync(), CheckIds.Settings).Alerting);
    }

    [Fact]
    public async Task A_shut_gate_is_a_fault_after_fifteen_minutes_and_the_clock_survives_a_restart()
    {
        _rig.HasCertificate = true;
        _rig.Gate = new GateState(false, GateReasons.NotTrusted, _rig.Time.GetUtcNow());

        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Listener).Alerting);
        _rig.Advance(TimeSpan.FromMinutes(14));
        _rig.Restart();
        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Listener).Alerting);
        _rig.Advance(TimeSpan.FromMinutes(1));
        var check = _rig.Check(await _rig.RunAsync(), CheckIds.Listener);
        Assert.True(check.Alerting);
        Assert.Equal(GateReasons.NotTrusted, check.Facts!["reason"]);
    }

    [Fact]
    public async Task Outside_not_reachable_is_a_fault_only_after_a_day()
    {
        _rig.Outside.Answer = new OutsideResult(OutsideState.NotReachable, "timeout");

        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Outside).Alerting);
        _rig.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Outside).Alerting);
        _rig.Advance(TimeSpan.FromMinutes(1));
        Assert.True(_rig.Check(await _rig.RunAsync(), CheckIds.Outside).Alerting);
    }

    [Fact]
    public async Task A_connected_port_is_never_called_working()
    {
        _rig.Outside.Answer = new OutsideResult(OutsideState.PortOpen, "connected");

        var check = _rig.Check(await _rig.RunAsync(), CheckIds.Outside);

        Assert.Equal("port_open", check.Code);
    }

    [Fact]
    public async Task The_outside_check_is_asked_about_the_forwarded_port()
    {
        var seen = new List<int>();
        _rig.Forwards.Add(Forward(external: 8443));
        var env = new HealthEnvironment
        {
            Config = () => _rig.Config,
            HasCertificate = () => false,
            Edge = () => null,
            Gate = () => null,
            StoredThumbprint = () => null,
            Drift = () => _rig.Drift,
            Forwards = () => _rig.Forwards,
            Leases = () => null,
            Sync = () => null,
            Outside = new PortSpy(seen),
            Alerts = _rig.Sink,
            Folder = () => _rig.Folder,
            Time = _rig.Time,
        };
        using var service = new HealthService(env, _rig.Log);

        await service.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { 8443 }, seen);
    }

    [Fact]
    public async Task The_address_check_follows_the_syncs_own_grace()
    {
        var now = _rig.Time.GetUtcNow();
        _rig.Sync = new SyncStatus(SyncOutcome.NoPublicAddress, now, now - TimeSpan.FromMinutes(30), false);
        Assert.False(_rig.Check(await _rig.RunAsync(), CheckIds.Address).Alerting);

        _rig.Sync = new SyncStatus(SyncOutcome.NoPublicAddress, now, now - TimeSpan.FromHours(1), false);
        var check = _rig.Check(await _rig.RunAsync(), CheckIds.Address);
        Assert.True(check.Alerting);
        Assert.Equal("no_public_address", check.Code);
    }

    [Fact]
    public async Task A_router_forward_alerts_only_once_the_mapper_says_it_is_a_fault()
    {
        _rig.Forwards.Add(Forward());
        _rig.Leases = new LastLeaseRun(new[] { new ForwardReport(_rig.Forwards[0], ForwardOutcome.RouterMissing, Fault: false) }, _rig.Time.GetUtcNow());
        var inGrace = _rig.Check(await _rig.RunAsync(), CheckIds.Router);
        Assert.Equal(CheckState.Problem, inGrace.State);
        Assert.False(inGrace.Alerting);

        _rig.Leases = new LastLeaseRun(new[] { new ForwardReport(_rig.Forwards[0], ForwardOutcome.RouterMissing, Fault: true) }, _rig.Time.GetUtcNow());
        Assert.True(_rig.Check(await _rig.RunAsync(), CheckIds.Router).Alerting);
    }

    [Fact]
    public async Task No_forward_recorded_is_off_not_a_fault()
    {
        Assert.Equal(CheckState.Off, _rig.Check(await _rig.RunAsync(), CheckIds.Router).State);
    }

    [Fact]
    public async Task The_whole_lifecycle_reaches_the_activity_log_start_daily_and_clear()
    {
        _rig.Drift[0] = new DriftItem("1", NetworkKeys.KnownProxies, DriftState.Changed, null);
        await _rig.RunAsync();
        _rig.Advance(TimeSpan.FromMinutes(10));
        await _rig.RunAsync();
        _rig.Advance(TimeSpan.FromDays(1));
        await _rig.RunAsync();
        _rig.Drift[0] = new DriftItem("1", NetworkKeys.KnownProxies, DriftState.InEffect, null);
        _rig.Advance(TimeSpan.FromMinutes(10));
        await _rig.RunAsync();
        await _rig.RunAsync();

        Assert.Equal(new[] { AlertKind.Started, AlertKind.Still, AlertKind.Cleared }, _rig.Sink.All.Select(a => a.Kind));
    }

    [Fact]
    public async Task The_report_is_saved_with_the_public_address_hashed_and_read_back_after_a_restart()
    {
        _rig.Watch.SetOwn(IPAddress.Parse("203.0.113.7"));

        var report = await _rig.RunAsync();
        _rig.Restart();

        var saved = File.ReadAllText(Path.Combine(_rig.Folder, "health.json"));
        Assert.DoesNotContain("203.0.113.7", saved, StringComparison.Ordinal);
        Assert.StartsWith("ip:", report.PublicAddressHash, StringComparison.Ordinal);
        var last = _rig.Service.Last;
        Assert.NotNull(last);
        Assert.Equal(report.CheckedAt, last!.CheckedAt);
        Assert.Equal(report.PublicAddressHash, last.PublicAddressHash);
        Assert.False(_rig.Service.IsStale(last));
        _rig.Advance(TimeSpan.FromMinutes(61));
        Assert.True(_rig.Service.IsStale(last));
    }

    [Fact]
    public async Task Two_callers_at_once_share_one_run()
    {
        var runs = await Task.WhenAll(_rig.Service.RunAsync(CancellationToken.None), _rig.Service.RunAsync(CancellationToken.None));

        Assert.Same(runs[0], runs[1]);
    }

    private sealed class PortSpy : IOutsideCheck
    {
        private readonly List<int> _seen;

        internal PortSpy(List<int> seen) => _seen = seen;

        public Task<OutsideResult> CheckAsync(string host, int port, CancellationToken ct)
        {
            _seen.Add(port);
            return Task.FromResult(new OutsideResult(OutsideState.NotChecked));
        }
    }
}

/// <summary>The served-certificate check (A1) against a real Edge over loopback TLS.</summary>
[Collection(RegistryCollection.Name)]
public sealed class ServedCertificateTests : IAsyncLifetime
{
    private EdgeRig _edge = null!;

    public async Task InitializeAsync() => _edge = await EdgeRig.StartAsync();

    public async Task DisposeAsync() => await _edge.DisposeAsync();

    private async Task<ServedCertificateFacts> CheckAsync(string? stored = null, DateTimeOffset? now = null)
    {
        var at = _edge.Service!.LoopbackEndPoint;
        return await ServedCertificate.CheckAsync(at, _edge.Config.Hostname, stored ?? _edge.Certificate!.Leaf.Thumbprint, now ?? DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Fact]
    public async Task The_certificate_being_served_for_our_name_is_ok()
    {
        await _edge.StartEdgeAsync();

        var facts = await CheckAsync();

        Assert.Equal(CertificateCodes.Ok, facts.Code);
        Assert.Equal(_edge.Certificate!.Leaf.Thumbprint, facts.Thumbprint);
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_caught()
    {
        _edge.Certificate = EdgeCerts.Make("someone-else.example.com");
        await _edge.StartEdgeAsync();

        Assert.Equal(CertificateCodes.WrongName, (await CheckAsync()).Code);
    }

    [Fact]
    public async Task An_expired_certificate_is_caught()
    {
        _edge.Certificate = EdgeCerts.Make(notBefore: DateTimeOffset.UtcNow.AddDays(-90), notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        await _edge.StartEdgeAsync();

        Assert.Equal(CertificateCodes.Expired, (await CheckAsync()).Code);
    }

    [Fact]
    public async Task Serving_something_the_store_does_not_hold_is_caught()
    {
        await _edge.StartEdgeAsync();

        Assert.Equal(CertificateCodes.NotStored, (await CheckAsync(stored: "0000")).Code);
    }

    [Fact]
    public async Task The_alert_starts_fourteen_days_before_expiry_not_before()
    {
        await _edge.StartEdgeAsync();
        var notAfter = new DateTimeOffset(_edge.Certificate!.Leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);

        Assert.Equal(CertificateCodes.Ok, (await CheckAsync(now: notAfter - TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1))).Code);
        Assert.Equal(CertificateCodes.ExpiresSoon, (await CheckAsync(now: notAfter - TimeSpan.FromDays(14))).Code);
        Assert.Equal(CertificateCodes.Expired, (await CheckAsync(now: notAfter)).Code);
    }

    [Fact]
    public async Task Edge_down_is_a_fault_at_once()
    {
        // Nothing to connect to: the endpoint Edge would have, after it has stopped.
        await _edge.StartEdgeAsync();
        var at = _edge.Service!.LoopbackEndPoint;
        await _edge.StopEdgeAsync();

        var facts = await ServedCertificate.CheckAsync(at, _edge.Config.Hostname, null, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(CertificateCodes.ListenerDown, facts.Code);
        Assert.Equal(CertificateCodes.ListenerDown, (await ServedCertificate.CheckAsync(null, _edge.Config.Hostname, null, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Through_the_health_run_a_wrong_name_alerts_and_the_report_says_so()
    {
        _edge.Certificate = EdgeCerts.Make("someone-else.example.com");
        await _edge.StartEdgeAsync();
        using var rig = new HealthRig();
        rig.Config.Hostname = _edge.Config.Hostname;
        rig.HasCertificate = true;
        rig.EdgeAt = _edge.Service!.LoopbackEndPoint;
        rig.Stored = _edge.Certificate.Leaf.Thumbprint;
        rig.Restart();

        var report = await rig.RunAsync();

        var check = rig.Check(report, CheckIds.Certificate);
        Assert.Equal(CertificateCodes.WrongName, check.Code);
        Assert.True(check.Alerting);
        Assert.Equal(Overall.Problem, report.Overall);
        Assert.Contains("different name", Assert.Single(rig.Sink.All).Overview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Through_the_health_run_edge_down_alerts_at_once()
    {
        using var rig = new HealthRig();
        rig.HasCertificate = true;
        rig.EdgeAt = null;
        rig.Gate = null;
        rig.Restart();

        var report = await rig.RunAsync();

        Assert.True(rig.Check(report, CheckIds.Certificate).Alerting);
        Assert.Equal(CertificateCodes.ListenerDown, rig.Check(report, CheckIds.Certificate).Code);

        // One fault, one line: the listener check says the same thing without a second alert line.
        Assert.False(rig.Check(report, CheckIds.Listener).Alerting);
        Assert.Contains("listener isn't running", Assert.Single(rig.Sink.All).Overview, StringComparison.Ordinal);
    }
}

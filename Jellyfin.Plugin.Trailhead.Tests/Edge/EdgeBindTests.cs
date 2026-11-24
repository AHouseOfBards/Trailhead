using System.Net;
using System.Text.Json;
using Campground.Shared.Registry;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>Edge's ports (03 §3b): one reservation table, a journalled move when the port is taken, 443 when allowed, and a stop that lets go.</summary>
[Collection(RegistryCollection.Name)]
public sealed class EdgeBindTests : IAsyncLifetime
{
    private EdgeRig _rig = null!;

    public async Task InitializeAsync() => _rig = await EdgeRig.StartAsync();

    public async Task DisposeAsync()
    {
        await _rig.DisposeAsync();
        CampgroundLifetime.ResetForTests();
    }

    [Fact]
    public async Task The_first_port_is_reserved_in_the_table_and_journalled_and_serves()
    {
        var ports = FreePorts.Take(2);

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.Equal(ports[0], edge.Port);
        Assert.Equal(ports[0], _rig.Ports.EdgePort());
        var entry = Assert.Single(_rig.Journal.Entries(), e => e.Kind == JournalKinds.PortReservation);
        Assert.Null(entry.Before);
        Assert.Equal(ports[0], entry.Written!.Value.GetInt32());
        Assert.False(FreePorts.IsFree(ports[0]));
    }

    [Fact]
    public async Task A_taken_port_moves_Edge_to_the_next_one_as_one_journalled_step()
    {
        var ports = FreePorts.Take(3);
        _rig.Ports.ReserveEdge(ports[0]);
        using var squatter = FreePorts.Hold(ports[0]);

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.Equal(ports[1], edge.Port);
        Assert.Equal(ports[1], _rig.Ports.EdgePort());
        var entry = Assert.Single(_rig.Journal.Entries(), e => e.Kind == JournalKinds.PortReservation);
        // The journal keeps the first "before": what was there before Trailhead reserved anything.
        Assert.Null(entry.Before);
        Assert.Equal(ports[1], entry.Written!.Value.GetInt32());
        Assert.Contains(_rig.Log.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains(ports[1].ToString(), StringComparison.Ordinal));
        using var client = _rig.Client();
        using var ok = await client.Http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task The_reserved_port_wins_over_the_list_so_a_restart_never_moves_it()
    {
        var ports = FreePorts.Take(3);
        _rig.Ports.ReserveEdge(ports[1]);

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.Equal(ports[1], edge.Port);
    }

    [Fact]
    public async Task The_reserved_port_is_retried_before_Edge_moves_off_it()
    {
        var ports = FreePorts.Take(2);
        _rig.Ports.ReserveEdge(ports[0]);
        var squatter = FreePorts.Hold(ports[0]);
        var options = _rig.Options(ports) is var o ? new EdgeOptions { Ports = o.Ports, ListenAddress = o.ListenAddress, ExtraPort = null, BindRetry = TimeSpan.FromSeconds(5), BindRetryStep = TimeSpan.FromMilliseconds(50), RecheckOpen = o.RecheckOpen, RecheckClosed = o.RecheckClosed } : null;

        // The old copy lets go half a second in, as an in-process restart's would.
        _ = Task.Delay(500).ContinueWith(_ => squatter.Dispose(), TaskScheduler.Default);
        var edge = await _rig.StartEdgeAsync(options);

        Assert.Equal(ports[0], edge.Port);
        Assert.Equal(ports[0], _rig.Ports.EdgePort());
    }

    [Fact]
    public async Task No_free_port_leaves_Edge_off_and_says_so()
    {
        var ports = FreePorts.Take(2);
        using var a = FreePorts.Hold(ports[0]);
        using var b = FreePorts.Hold(ports[1]);

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.False(edge.IsRunning);
        Assert.Equal(EdgeNotRunning.NoFreePort, edge.NotRunningReason);
        Assert.Null(_rig.Ports.EdgePort());
        Assert.Empty(_rig.Journal.Entries());
    }

    [Fact]
    public async Task Jellyfins_own_port_is_never_a_candidate()
    {
        var ports = new[] { _rig.Jellyfin.Port }.Concat(FreePorts.Take(1)).ToArray();

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.Equal(ports[1], edge.Port);
        using var direct = new HttpClient();
        using var still = await direct.GetAsync("http://127.0.0.1:" + _rig.Jellyfin.Port + "/echo");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task The_extra_port_is_served_too_when_it_can_be_bound()
    {
        var ports = FreePorts.Take(2);
        var extra = FreePorts.Take(1)[0];

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports, extra));

        Assert.Equal(extra, edge.ExtraPort);
        using var client = new EdgeClient(extra, IPAddress.Parse("127.0.0.5"), null);
        using var ok = await client.Http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task An_extra_port_that_cant_be_bound_is_logged_once_and_the_main_port_still_serves()
    {
        var ports = FreePorts.Take(2);
        var extra = FreePorts.Take(1)[0];
        using var held = FreePorts.Hold(extra);

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports, extra));

        Assert.True(edge.IsRunning);
        Assert.Null(edge.ExtraPort);
        Assert.Equal(ports[0], edge.Port);
        Assert.Single(_rig.Log.Entries, e => e.Text.Contains(extra.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Log.Entries, e => e.Level >= LogLevel.Error);

        // A port held by someone else isn't a move: the table still holds the main port only.
        Assert.Equal(ports[0], _rig.Ports.EdgePort());
    }

    [Fact]
    public async Task Stopping_lets_go_of_every_port_and_a_new_copy_binds_the_same_one()
    {
        var ports = FreePorts.Take(2);
        var extra = FreePorts.Take(1)[0];
        var edge = await _rig.StartEdgeAsync(_rig.Options(ports, extra));
        Assert.False(FreePorts.IsFree(ports[0]));
        Assert.False(FreePorts.IsFree(extra));

        await _rig.StopEdgeAsync();

        Assert.True(FreePorts.IsFree(ports[0]), "the main port is still held");
        Assert.True(FreePorts.IsFree(extra), "the extra port is still held");
        Assert.False(edge.IsRunning);

        // The in-process restart: a new copy with the same table.
        var again = await _rig.StartEdgeAsync(_rig.Options(ports, extra));
        Assert.Equal(ports[0], again.Port);
        Assert.Single(_rig.Journal.Entries(), e => e.Kind == JournalKinds.PortReservation);
    }

    [Fact]
    public async Task A_journal_that_cant_record_the_port_keeps_Edge_off()
    {
        var ports = FreePorts.Take(2);
        File.WriteAllText(_rig.Journal.FilePath, "not json");
        _ = _rig.Journal.Entries();

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.False(edge.IsRunning);
        Assert.Equal(EdgeNotRunning.JournalUnavailable, edge.NotRunningReason);
        Assert.True(FreePorts.IsFree(ports[0]));
    }

    [Fact]
    public async Task Not_set_up_binds_nothing_and_makes_no_file()
    {
        // Do no harm (a): installed, setup not run.
        _rig.Config.Hostname = string.Empty;
        var ports = FreePorts.Take(2);
        var before = Directory.EnumerateFileSystemEntries(_rig.Folder, "*", SearchOption.AllDirectories).ToList();

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports, FreePorts.Take(1)[0]));

        Assert.False(edge.IsRunning);
        Assert.Equal(EdgeNotRunning.NotSetUp, edge.NotRunningReason);
        Assert.True(FreePorts.IsFree(ports[0]));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_rig.Folder, "*", SearchOption.AllDirectories).ToList());
    }

    [Fact]
    public async Task No_certificate_yet_means_no_listener()
    {
        _rig.Certificate = null;
        var ports = FreePorts.Take(2);

        var edge = await _rig.StartEdgeAsync(_rig.Options(ports));

        Assert.False(edge.IsRunning);
        Assert.Equal(EdgeNotRunning.NoCertificate, edge.NotRunningReason);
        Assert.True(FreePorts.IsFree(ports[0]));
    }

    [Fact]
    public async Task The_first_certificate_arriving_later_starts_Edge()
    {
        var stored = _rig.Certificate;
        _rig.Certificate = null;
        var edge = await _rig.StartEdgeAsync(_rig.Options(FreePorts.Take(2)));
        Assert.False(edge.IsRunning);

        _rig.Certificate = stored;
        await edge.EnsureStartedAsync(CancellationToken.None);

        Assert.True(edge.IsRunning);
    }

    [Fact]
    public async Task An_environment_that_throws_leaves_Edge_off_and_throws_nothing()
    {
        var lifetime = new DrivenLifetime();
        var loggers = LoggerFactory.Create(_ => { });
        var edge = new EdgeService(loggers, lifetime, new EdgeIdentity(), _rig.Ports, null, () => throw new InvalidOperationException("no Jellyfin"), _rig.Options(), TimeProvider.System);

        await edge.StartAsync(CancellationToken.None);
        lifetime.Start();
        await edge.StartTask;
        lifetime.StopApplication();
        await edge.StopAsync(CancellationToken.None);

        Assert.False(edge.IsRunning);
        Assert.Equal(EdgeNotRunning.Stopping, edge.NotRunningReason);
    }
}

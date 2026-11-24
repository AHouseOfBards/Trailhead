using System.Net;
using System.Net.NetworkInformation;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Network;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Router;

/// <summary>The router services and the lease task, through the real registrator and the real finder.</summary>
[Collection(RegistryCollection.Name)]
public sealed class RouterServicesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public RouterServicesTests()
    {
        CampgroundPaths.OverrideForTests(_folder.File("campground"));
    }

    public void Dispose() => _folder.Dispose();

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    [Fact]
    public void The_services_and_the_lease_task_resolve_the_way_Jellyfin_makes_them_and_do_no_work()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHostApplicationLifetime>(new NoLifetime());
        collection.AddSingleton(RenewalTestKit.Paths(_folder.File("jellyfin")));
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        using var provider = collection.BuildServiceProvider();
        var before = Snapshot();

        var services = provider.GetRequiredService<RouterServices>();
        var task = ActivatorUtilities.CreateInstance<RenewForwardsTask>(provider);

        Assert.Same(services, provider.GetRequiredService<RouterServices>());
        Assert.NotNull(task);
        Assert.False(services.Built);
        Assert.Equal(before, Snapshot());
        Assert.Empty(provider.GetServices<IScheduledTask>());
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_folder.Path, "*", SearchOption.AllDirectories).Order().ToArray();

    [Fact]
    public void The_lease_task_runs_every_ten_minutes_and_at_start()
    {
        var task = new RenewForwardsTask(new RouterServices(new ListLogger(), new RouterOverrides { DataFolder = _folder.Path }), new TypedLogger<RenewForwardsTask>(new ListLogger()));

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromMinutes(10).Ticks);
        Assert.Contains(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
    }

    private sealed class CountingFinder : IRouterFinder
    {
        internal int Calls { get; private set; }

        internal Exception? Throw { get; set; }

        public Task<RouterSearchResult> FindAsync(CancellationToken ct)
        {
            Calls++;
            return Throw is null ? Task.FromResult(new RouterSearchResult(RouterStatus.NoReply, null)) : Task.FromException<RouterSearchResult>(Throw);
        }
    }

    [Fact]
    public async Task With_nothing_recorded_the_task_does_not_search_for_the_router()
    {
        var finder = new CountingFinder();
        using var services = new RouterServices(new ListLogger(), new RouterOverrides { DataFolder = _folder.Path, Finder = finder });
        var task = new RenewForwardsTask(services, new TypedLogger<RenewForwardsTask>(new ListLogger()));

        await task.ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        Assert.Equal(0, finder.Calls);
    }

    [Fact]
    public async Task The_task_never_throws_and_logs_only_the_error_type()
    {
        var finder = new CountingFinder { Throw = new InvalidOperationException("secret router text 203.0.113.7") };
        using var services = new RouterServices(new ListLogger(), new RouterOverrides { DataFolder = _folder.Path, Finder = finder });
        new ForwardsStore(Path.Combine(_folder.Path, "forwards.json")).Put(new ForwardRecord("a", RouterMapper.TrailheadOwner, "TCP", 443, 8443, "192.168.1.50", ForwardStates.Active, 0, DateTimeOffset.UtcNow, null, null));
        var log = new ListLogger();
        var task = new RenewForwardsTask(services, new TypedLogger<RenewForwardsTask>(log));

        await task.ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        Assert.Equal(1, finder.Calls);
        Assert.Contains("InvalidOperationException", log.All);
        Assert.DoesNotContain("secret", log.All);
        Assert.DoesNotContain("203.0.113.7", log.All);
    }

    [Fact]
    public async Task A_cancelled_run_is_a_cancel()
    {
        using var services = new RouterServices(new ListLogger(), new RouterOverrides { DataFolder = _folder.Path, Finder = new CountingFinder() });
        new ForwardsStore(Path.Combine(_folder.Path, "forwards.json")).Put(new ForwardRecord("a", RouterMapper.TrailheadOwner, "TCP", 443, 8443, "192.168.1.50", ForwardStates.Active, 0, DateTimeOffset.UtcNow, null, null));
        var task = new RenewForwardsTask(services, new TypedLogger<RenewForwardsTask>(new ListLogger()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new RecordingProgress(), cancelled.Token));
    }

    /// <summary>The real finder, with the answer's address moved onto a LAN the fake view has (the fake answers from loopback).</summary>
    private sealed class OnLan : IRouterFinder
    {
        private readonly IRouterFinder _inner;

        internal OnLan(IRouterFinder inner) => _inner = inner;

        public async Task<RouterSearchResult> FindAsync(CancellationToken ct)
        {
            var found = await _inner.FindAsync(ct);
            return found.Device is null ? found : found with { Device = found.Device with { Address = IPAddress.Parse("192.168.1.1") } };
        }
    }

    private sealed class View : INetworkView
    {
        public IReadOnlyList<Nic> Interfaces() => new[]
        {
            new Nic("{a}", "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, true, new[] { new NicAddress(IPAddress.Parse("172.24.80.1"), 20) }, Array.Empty<IPAddress>()),
            new Nic("{b}", "Ethernet", "Realtek", NetworkInterfaceType.Ethernet, true, new[] { new NicAddress(IPAddress.Parse("192.168.1.50"), 24) }, new[] { IPAddress.Parse("192.168.1.1") }),
        };
    }

    [Fact]
    public async Task End_to_end_the_mapping_points_at_the_LAN_address_and_the_task_keeps_its_lease()
    {
        await using var igd = await FakeIgd.StartAsync();
        igd.RefusePermanentWith = UpnpCodes.OnlyPermanentLeases;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        igd.Clock = () => time.GetUtcNow();
        using var http = new HttpClient();
        using var services = new RouterServices(new ListLogger(), new RouterOverrides
        {
            DataFolder = _folder.Path,
            Finder = new OnLan(new RouterFinder(http, () => new[] { igd.Plan }, TimeSpan.FromMilliseconds(700))),
            View = new View(),
            Time = time,
            RouterLock = new SemaphoreSlim(1, 1),
        });
        var task = new RenewForwardsTask(services, new TypedLogger<RenewForwardsTask>(new ListLogger()));

        var mapped = await services.Mapper.MapAsync(443, 8443, "TCP", CancellationToken.None);
        time.Advance(TimeSpan.FromDays(4));
        await task.ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        Assert.Equal(MapOutcome.MappedWithLease, mapped.Outcome);
        Assert.Equal("192.168.1.50", Assert.Single(igd.Mappings).Client);
        Assert.Equal(ForwardOutcome.Renewed, Assert.Single(services.LastRun!.Forwards).Outcome);
        Assert.Equal(time.GetUtcNow(), igd.Mappings.Single().Added);
    }

    [Fact]
    public void After_dispose_nothing_new_is_built()
    {
        var services = new RouterServices(new ListLogger(), new RouterOverrides { DataFolder = _folder.Path });
        services.Dispose();

        Assert.Throws<OperationCanceledException>(() => services.Mapper);
    }
}

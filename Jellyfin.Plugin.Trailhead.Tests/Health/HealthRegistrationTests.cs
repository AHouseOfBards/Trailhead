using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

/// <summary>M4's parts through the real <see cref="PluginServiceRegistrator"/>, built the way Jellyfin builds them.</summary>
[Collection(RegistryCollection.Name)]
public sealed class HealthRegistrationTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public HealthRegistrationTests()
    {
        RenewalTestKit.ForgetPlugin();
        CampgroundPaths.OverrideForTests(_folder.File("campground"));
    }

    public void Dispose()
    {
        RenewalTestKit.ForgetPlugin();
        _folder.Dispose();
    }

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private ServiceProvider Compose()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHostApplicationLifetime>(new NoLifetime());
        collection.AddSingleton(RenewalTestKit.Paths(_folder.File("jellyfin")));
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        return collection.BuildServiceProvider();
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_folder.Path, "*", SearchOption.AllDirectories).Order().ToArray();

    [Fact]
    public void The_services_and_both_tasks_resolve_and_do_no_work()
    {
        using var provider = Compose();
        var before = Snapshot();

        var services = provider.GetRequiredService<HealthServices>();
        var sync = ActivatorUtilities.CreateInstance<AddressSyncTask>(provider);
        var health = ActivatorUtilities.CreateInstance<HealthTask>(provider);
        var controller = ActivatorUtilities.CreateInstance<Api.HealthController>(provider);

        Assert.NotNull(sync);
        Assert.NotNull(health);
        Assert.NotNull(controller);
        Assert.Same(services, provider.GetRequiredService<HealthServices>());
        Assert.False(services.Built);
        Assert.Equal(before, Snapshot());

        // Found by Jellyfin's assembly scan, so not registered (a registration would be a second, unused copy).
        Assert.Empty(provider.GetServices<IScheduledTask>());
    }

    [Fact]
    public void Edge_answers_the_same_phone_test_the_admin_route_makes_links_for()
    {
        using var provider = Compose();

        var edge = provider.GetRequiredService<EdgeService>();
        var services = provider.GetRequiredService<HealthServices>();

        Assert.NotNull(edge.Phone);
        Assert.Same(edge.Phone, services.Phone);
        Assert.Same(provider.GetRequiredService<PhoneTest>(), edge.Phone);
    }

    [Fact]
    public async Task Not_set_up_the_tasks_run_without_a_call_out_and_without_throwing()
    {
        var plugin = new Plugin(RenewalTestKit.Paths(_folder.File("jellyfin")), new RenewalTestKit.XmlStub());
        RenewalTestKit.SwapConfiguration(plugin, new PluginConfiguration());
        using var provider = Compose();
        var sync = ActivatorUtilities.CreateInstance<AddressSyncTask>(provider);
        var health = ActivatorUtilities.CreateInstance<HealthTask>(provider);

        await sync.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        await health.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var services = provider.GetRequiredService<HealthServices>();
        Assert.Equal(SyncOutcome.NotSetUp, services.Sync.Last!.Outcome);
        Assert.Equal(Overall.NotSetUp, services.Health.Last!.Overall);
    }

    [Fact]
    public void Both_tasks_run_every_ten_minutes_and_the_sync_at_start_too()
    {
        using var provider = Compose();
        var sync = ActivatorUtilities.CreateInstance<AddressSyncTask>(provider).GetDefaultTriggers().ToList();
        var health = ActivatorUtilities.CreateInstance<HealthTask>(provider).GetDefaultTriggers().ToList();

        Assert.Contains(sync, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromMinutes(10).Ticks);
        Assert.Contains(sync, t => t.Type == TaskTriggerInfoType.StartupTrigger);
        Assert.Contains(health, t => t.Type == TaskTriggerInfoType.IntervalTrigger && t.IntervalTicks == TimeSpan.FromMinutes(10).Ticks);
    }
}

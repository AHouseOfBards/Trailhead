using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Renewal;

/// <summary>
/// The renewal wiring through the real <see cref="PluginServiceRegistrator"/>, built the way Jellyfin builds a task
/// (<c>ActivatorUtilities</c> against the service provider; ApplicationHost.CreateInstanceSafe at v12.0). Nothing here
/// gets as far as a network call: the settings it sets either don't set the plugin up or hold a key that won't open.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class RenewalRegistrationTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public RenewalRegistrationTests()
    {
        RenewalTestKit.ForgetPlugin();
        CampgroundPaths.OverrideForTests(_folder.File("campground"));
    }

    public void Dispose()
    {
        RenewalTestKit.ForgetPlugin();
        _folder.Dispose();
    }

    private ServiceProvider Compose(IApplicationPaths? paths = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHostApplicationLifetime>(new NoLifetime());
        collection.AddSingleton(paths ?? RenewalTestKit.Paths(_folder.File("jellyfin")));
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        return collection.BuildServiceProvider();
    }

    private Plugin MakePlugin(PluginConfiguration? config = null)
    {
        var plugin = new Plugin(RenewalTestKit.Paths(_folder.File("jellyfin")), new RenewalTestKit.XmlStub());
        if (config is not null)
        {
            RenewalTestKit.SwapConfiguration(plugin, config);
        }

        return plugin;
    }

    [Fact]
    public void The_services_and_the_task_resolve_the_way_Jellyfin_makes_them_and_do_no_work()
    {
        // Saved settings that WOULD build a manager, so a build at construction would show as files.
        MakePlugin(new PluginConfiguration { Hostname = "trailhead-test.example.com", DynuKey = RenewalTestKit.Sealed(1, FakeDynu.Key) });
        using var provider = Compose();
        var before = Snapshot();

        var services = provider.GetRequiredService<CertificateServices>();
        var task = ActivatorUtilities.CreateInstance<RenewCertificateTask>(provider);

        Assert.Same(services, provider.GetRequiredService<CertificateServices>());
        Assert.NotNull(task);
        Assert.Equal(0, services.Builds);
        Assert.Equal(before, Snapshot());
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_folder.Path, "*", SearchOption.AllDirectories).Order().ToArray();

    [Fact]
    public void Jellyfin_finds_exactly_one_task_in_the_plugin_by_scanning_its_exported_types()
    {
        // ApplicationHost.GetTypes takes GetExportedTypes() and keeps concrete, non-generic classes.
        var found = typeof(Plugin).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericType && typeof(IScheduledTask).IsAssignableFrom(t))
            .ToList();

        Assert.Equal(new[] { typeof(RenewCertificateTask) }, found);
    }

    [Fact]
    public void The_task_is_not_also_registered_in_DI_where_Jellyfin_would_make_a_second_copy()
    {
        using var provider = Compose();

        Assert.Empty(provider.GetServices<IScheduledTask>());
    }

    [Fact]
    public async Task Settings_are_read_from_the_plugin_each_run_and_follow_a_swapped_configuration()
    {
        var plugin = MakePlugin();
        using var provider = Compose();
        var services = provider.GetRequiredService<CertificateServices>();

        var none = await services.RenewAsync(CancellationToken.None);

        // What an admin's save does: a new configuration object replaces the old one.
        RenewalTestKit.SwapConfiguration(plugin, new PluginConfiguration { Hostname = "trailhead-test.example.com" });
        var noKey = await services.RenewAsync(CancellationToken.None);

        // A key sealed by some other machine's key file: it won't open here, so still nothing to renew.
        RenewalTestKit.SwapConfiguration(plugin, new PluginConfiguration { Hostname = "trailhead-test.example.com", DynuKey = RenewalTestKit.Sealed(5, FakeDynu.Key) });
        var elsewhere = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.NotSetUp, none.State);
        Assert.Contains("no web address", none.Message);
        Assert.Equal(RenewalState.NotSetUp, noKey.State);
        Assert.Contains("no Dynu key", noKey.Message);
        Assert.Equal(RenewalState.NotSetUp, elsewhere.State);
        Assert.Contains("can't be opened", elsewhere.Message);
        Assert.Equal(0, services.Builds);
    }

    [Fact]
    public async Task With_no_plugin_instance_at_all_it_is_not_set_up()
    {
        using var provider = Compose();
        var services = provider.GetRequiredService<CertificateServices>();

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.NotSetUp, report.State);
    }

    [Fact]
    public async Task The_provider_disposing_the_services_makes_a_late_run_a_cancel()
    {
        using var provider = Compose();
        var services = provider.GetRequiredService<CertificateServices>();

        provider.Dispose();

        // The container disposes singletons: a late run is a cancel, not a build.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.RenewAsync(CancellationToken.None));
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
}

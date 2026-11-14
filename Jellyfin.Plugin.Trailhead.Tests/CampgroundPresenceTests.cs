using Campground.Shared.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests;

/// <summary>
/// Trailhead joins Campground through the real <see cref="PluginServiceRegistrator"/>: it registers with the host's
/// lifetime, announces itself, withdraws on stop, and never lets a failure in any of that reach Jellyfin.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class CampgroundPresenceTests : IDisposable
{
    public CampgroundPresenceTests() => Reset();

    public void Dispose() => Reset();

    [Fact]
    public async Task Announces_Trailhead_once_the_host_starts()
    {
        var lifetime = new FakeLifetime();
        await using var services = Compose(lifetime);

        await StartAll(services);

        var me = Assert.Single(PluginRegistry.List(), p => p.Id == PluginIds.Trailhead);
        Assert.Equal("Trailhead", me.Name);
        Assert.Equal(typeof(Plugin).Assembly.GetName().Version!.ToString(), me.Version);
        Assert.Equal(CampgroundShared.Version, me.SharedVersion);
        Assert.True(me.Status.Enabled);
        Assert.NotNull(CampgroundLifetime.CopyGeneration);
    }

    [Fact]
    public async Task Withdraws_its_announcement_when_the_host_stops()
    {
        var lifetime = new FakeLifetime();
        await using var services = Compose(lifetime);
        await StartAll(services);
        Assert.True(PluginRegistry.IsInstalled(PluginIds.Trailhead));

        lifetime.StopApplication();

        Assert.False(PluginRegistry.IsInstalled(PluginIds.Trailhead));
    }

    [Fact]
    public async Task A_failing_lifetime_never_stops_Jellyfin_starting()
    {
        await using var services = Compose(new ThrowingLifetime());

        // Fail open: start-up goes on, and Trailhead says nothing rather than half-announcing itself.
        await StartAll(services);

        Assert.False(PluginRegistry.IsInstalled(PluginIds.Trailhead));
    }

    private static ServiceProvider Compose(IHostApplicationLifetime lifetime)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(lifetime);

        // Trailhead's registrator doesn't use the application host yet; when it does, these tests need a real one.
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        return collection.BuildServiceProvider();
    }

    private static async Task StartAll(IServiceProvider services)
    {
        var hosted = services.GetServices<IHostedService>().ToList();

        // A registrator that registers nothing would pass the fail-open test for the wrong reason.
        Assert.NotEmpty(hosted);
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }
    }

    private static void Reset()
    {
        PluginRegistry.Withdraw(PluginIds.Trailhead);
        CampgroundLifetime.ResetForTests();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }
    }

    private sealed class ThrowingLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => throw new InvalidOperationException("broken host");

        public CancellationToken ApplicationStopping => throw new InvalidOperationException("broken host");

        public CancellationToken ApplicationStopped => throw new InvalidOperationException("broken host");

        public void StopApplication() => throw new InvalidOperationException("broken host");
    }
}

/// <summary>The Campground registry is process-wide, so every test that touches it runs serially.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegistryCollection
{
    public const string Name = "Campground registry";
}

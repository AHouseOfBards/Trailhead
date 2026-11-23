using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

/// <summary>
/// The settings services through the real <see cref="PluginServiceRegistrator"/>, in a container shaped like
/// Jellyfin's: its configuration manager, XML serializer, network manager and paths as singletons, the system manager
/// scoped (Emby.Server.Implementations/ApplicationHost.cs:505-532, v12.4), and the forwarded-headers options built
/// from network.xml at start. Scope validation is on, so a singleton holding a scoped service fails here.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class SettingsRegistrationTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeJellyfinConfig _config;

    public SettingsRegistrationTests()
    {
        RenewalTestKit.ForgetPlugin();
        CampgroundPaths.OverrideForTests(_folder.File("campground"));
        _config = new FakeJellyfinConfig(_folder.File("jellyfin"));
        _config.WriteFile(JellyfinNetworkTests.Realistic());
    }

    public void Dispose()
    {
        RenewalTestKit.ForgetPlugin();
        _folder.Dispose();
    }

    private ServiceProvider Compose(bool withSystemManager = true, RecordingSystem? system = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOptions();
        collection.AddSingleton<IHostApplicationLifetime>(new NoLifetime());
        collection.AddSingleton(RenewalTestKit.Paths(_folder.File("jellyfin")));
        collection.AddSingleton<IConfigurationManager>(_config);
        collection.AddSingleton<IXmlSerializer>(new JellyfinXml());
        collection.AddSingleton(Wildcard());
        var start = _config.ReadFile();
        collection.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
        {
            var built = NetworkRig.LiveOptionsAsJellyfinBuildsThem(start);
            o.ForwardedHeaders = built.ForwardedHeaders;
            o.KnownProxies.Clear();
            foreach (var p in built.KnownProxies)
            {
                o.KnownProxies.Add(p);
            }

            o.KnownIPNetworks.Clear();
            foreach (var n in built.KnownIPNetworks)
            {
                o.KnownIPNetworks.Add(n);
            }
        });
        if (withSystemManager)
        {
            var recorder = system ?? new RecordingSystem();
            collection.AddScoped(_ => recorder.Proxy);
        }

        new PluginServiceRegistrator().RegisterServices(collection, null!);
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static INetworkManager Wildcard()
    {
        var proxy = DispatchProxy.Create<INetworkManager, NetworkProxy>();
        ((NetworkProxy)(object)proxy).Binds = new[] { new IPData(IPAddress.IPv6Any, null) };
        return proxy;
    }

    // Every file under the test folder, with a hash of its bytes.
    private string[] Snapshot() => Directory.EnumerateFiles(_folder.Path, "*", SearchOption.AllDirectories)
        .Order()
        .Select(f => f + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
        .ToArray();

    [Fact]
    public async Task Installed_and_started_with_setup_not_run_nothing_in_Jellyfins_config_changes()
    {
        var before = Snapshot();
        var dirsBefore = Directory.EnumerateDirectories(_folder.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        using (var provider = Compose())
        {
            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var h in hosted)
            {
                await h.StartAsync(CancellationToken.None);
            }

            Assert.NotNull(provider.GetRequiredService<Journal>());
            Assert.NotNull(provider.GetRequiredService<JellyfinNetwork>());
            Assert.NotNull(provider.GetRequiredService<IJellyfinRestart>());
            Assert.NotNull(provider.GetRequiredService<SetupProgress>());
            Assert.NotNull(provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value);

            foreach (var h in hosted)
            {
                await h.StopAsync(CancellationToken.None);
            }
        }

        Assert.Equal(before, Snapshot());
        Assert.Equal(dirsBefore, Directory.EnumerateDirectories(_folder.Path, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(0, _config.Saves);
    }

    [Fact]
    public void The_settings_services_are_singletons()
    {
        using var provider = Compose();

        Assert.Same(provider.GetRequiredService<Journal>(), provider.GetRequiredService<Journal>());
        Assert.Same(provider.GetRequiredService<JellyfinNetwork>(), provider.GetRequiredService<JellyfinNetwork>());
        Assert.Same(provider.GetRequiredService<IJellyfinRestart>(), provider.GetRequiredService<IJellyfinRestart>());
        Assert.Same(provider.GetRequiredService<SetupProgress>(), provider.GetRequiredService<SetupProgress>());
    }

    [Fact]
    public async Task The_wired_writer_journals_into_Trailheads_own_folder_and_reads_the_live_options()
    {
        // Jellyfin started with no Known proxies, so its live options have forwarding off.
        var start = JellyfinNetworkTests.Realistic();
        start.KnownProxies = Array.Empty<string>();
        _config.WriteFile(start);
        using var provider = Compose();
        var network = provider.GetRequiredService<JellyfinNetwork>();
        var address = ForwardingAddress.Current(provider.GetRequiredService<INetworkManager>(), _config);

        var result = await network.ApplyOneAsync(NetworkKeys.KnownProxies, new NetworkPlan(address!, "trailhead-test.example.com"), CancellationToken.None);
        var drift = network.CheckDrift();

        Assert.Equal(IPAddress.Loopback, address);
        Assert.Equal(SettingOutcome.Written, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_folder.File("campground"), "trailhead", "journal.json")));

        Assert.Equal(DriftState.SavedNotInEffect, Assert.Single(drift).State);
    }

    [Fact]
    public void Restart_is_the_system_managers_own_from_a_scope()
    {
        var system = new RecordingSystem();
        using var provider = Compose(system: system);

        var asked = provider.GetRequiredService<IJellyfinRestart>().Restart();

        Assert.True(asked);
        Assert.Equal(new[] { nameof(ISystemManager.Restart) }, system.Calls);
    }

    [Fact]
    public void Restart_without_a_system_manager_says_no_and_throws_nothing()
    {
        using var provider = Compose(withSystemManager: false);

        Assert.False(provider.GetRequiredService<IJellyfinRestart>().Restart());
    }

    [Fact]
    public void Restart_that_throws_says_no_and_throws_nothing()
    {
        var system = new RecordingSystem { Throw = true };
        using var provider = Compose(system: system);

        Assert.False(provider.GetRequiredService<IJellyfinRestart>().Restart());
    }

    [Fact]
    public void A_journal_resolved_with_no_paths_registered_fails_when_asked_for_not_at_registration()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        using var provider = collection.BuildServiceProvider();

        Assert.ThrowsAny<InvalidOperationException>(() => provider.GetRequiredService<Journal>());
    }

    internal sealed class RecordingSystem
    {
        internal RecordingSystem()
        {
            Proxy = DispatchProxy.Create<ISystemManager, SystemProxy>();
            ((SystemProxy)(object)Proxy).Owner = this;
        }

        internal ISystemManager Proxy { get; }

        internal List<string> Calls { get; } = new();

        internal bool Throw { get; init; }
    }

    internal class SystemProxy : DispatchProxy
    {
        internal RecordingSystem? Owner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Owner!.Throw)
            {
                throw new InvalidOperationException("no");
            }

            Owner.Calls.Add(targetMethod!.Name);
            return null;
        }
    }

    internal class NetworkProxy : DispatchProxy
    {
        internal IReadOnlyList<IPData> Binds { get; set; } = Array.Empty<IPData>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(INetworkManager.GetAllBindInterfaces) ? Binds : throw new NotSupportedException(targetMethod.Name);
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

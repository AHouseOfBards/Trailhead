using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Campground.Shared.Contracts;
using Campground.Shared.Registry;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using Jellyfin.Plugin.Trailhead.Tests.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>Edge through the real <see cref="PluginServiceRegistrator"/>, in a container shaped like Jellyfin's.</summary>
[Collection(RegistryCollection.Name)]
public sealed class EdgeRegistrationTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeJellyfinConfig _config;

    public EdgeRegistrationTests()
    {
        RenewalTestKit.ForgetPlugin();
        CampgroundLifetime.ResetForTests();
        CampgroundPaths.OverrideForTests(_folder.File("campground"));
        _config = new FakeJellyfinConfig(_folder.File("jellyfin"));
        _config.WriteFile(JellyfinNetworkTests.Realistic());
    }

    public void Dispose()
    {
        Capabilities.Withdraw(CapabilityNames.Https, PluginIds.Trailhead);
        CampgroundLifetime.ResetForTests();
        RenewalTestKit.ForgetPlugin();
        _folder.Dispose();
    }

    private ServiceProvider Compose(DrivenLifetime lifetime, bool withHost)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOptions();
        collection.AddSingleton<IHostApplicationLifetime>(lifetime);
        collection.AddSingleton(RenewalTestKit.Paths(_folder.File("jellyfin")));
        collection.AddSingleton<IConfigurationManager>(_config);
        collection.AddSingleton<IXmlSerializer>(new JellyfinXml());
        var network = DispatchProxy.Create<INetworkManager, SettingsRegistrationTests.NetworkProxy>();
        ((SettingsRegistrationTests.NetworkProxy)(object)network).Binds = new[] { new IPData(IPAddress.IPv6Any, null) };
        collection.AddSingleton(network);
        if (withHost)
        {
            collection.AddSingleton(DispatchProxy.Create<IServerApplicationHost, HostProxy>());
        }

        new PluginServiceRegistrator().RegisterServices(collection, null!);
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_folder.Path, "*", SearchOption.AllDirectories)
        .Order()
        .Select(f => File.Exists(f) ? f + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) : f)
        .ToArray();

    private static async Task<EdgeService> StartAllAsync(ServiceProvider provider, DrivenLifetime lifetime)
    {
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is EdgeService);
        foreach (var h in hosted)
        {
            await h.StartAsync(CancellationToken.None);
        }

        lifetime.Start();
        var edge = provider.GetRequiredService<EdgeService>();
        await edge.StartTask;
        return edge;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Installed_and_not_set_up_Edge_binds_nothing_makes_nothing_and_Jellyfin_starts(bool withHost)
    {
        var lifetime = new DrivenLifetime();
        var before = Snapshot();
        using var provider = Compose(lifetime, withHost);

        var edge = await StartAllAsync(provider, lifetime);
        lifetime.StopApplication();
        foreach (var h in provider.GetServices<IHostedService>().Reverse())
        {
            await h.StopAsync(CancellationToken.None);
        }

        Assert.False(edge.IsRunning);
        Assert.Null(edge.Port);
        Assert.Contains(edge.NotRunningReason, new[] { EdgeNotRunning.NotSetUp, EdgeNotRunning.StartFailed, EdgeNotRunning.Stopping });
        Assert.Equal(before, Snapshot());
        Assert.Equal(0, _config.Saves);
    }

    [Fact]
    public async Task A_container_without_Jellyfins_services_still_builds_and_starts_every_hosted_service()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IHostApplicationLifetime>(new DrivenLifetime());
        new PluginServiceRegistrator().RegisterServices(collection, null!);
        using var provider = collection.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var h in hosted)
        {
            await h.StartAsync(CancellationToken.None);
            await h.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, hosted.Count);
        Assert.DoesNotContain(hosted, h => h is EdgeService);
    }

    [Fact]
    public void The_hosted_service_is_the_singleton_and_the_echo_has_its_identity()
    {
        var lifetime = new DrivenLifetime();
        using var provider = Compose(lifetime, true);

        var hosted = provider.GetServices<IHostedService>().OfType<EdgeService>().Single();

        Assert.Same(provider.GetRequiredService<EdgeService>(), hosted);
        Assert.Same(provider.GetRequiredService<EdgeIdentity>(), provider.GetRequiredService<EdgeIdentity>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<Api.EdgeEchoController>(provider));
    }

    [Fact]
    public async Task Https_v1_answers_status_and_provision_through_the_registry()
    {
        var lifetime = new DrivenLifetime();
        using var provider = Compose(lifetime, true);
        await StartAllAsync(provider, lifetime);

        var status = await HttpsClient.StatusAsync();
        var provision = await HttpsClient.ProvisionAsync(new HttpsProvisionRequest("gametent", null));

        Assert.NotNull(status);
        Assert.False(status!.Configured);
        Assert.Null(status.PublicUrl);
        Assert.NotNull(status.Edge);
        Assert.False(status.Edge!.Active);
        Assert.False(status.Edge.Buffered);
        Assert.Null(status.Edge.Marker);
        Assert.NotNull(provision);
        Assert.False(provision!.Started);
        Assert.Equal("/jellyfin/web/#/configurationpage?name=Trailhead", provision.FollowUpUrl);
    }


    internal class HostProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_HttpPort" => 8096,
            "get_HttpsPort" => 8920,
            "get_ListenWithHttps" => false,
            "get_ApplicationVersionString" => "12.4.0",
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }
}

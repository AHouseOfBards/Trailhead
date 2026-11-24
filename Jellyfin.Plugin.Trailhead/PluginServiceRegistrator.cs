using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Settings;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>Registers Trailhead's services with Jellyfin's host.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<CampgroundPresence>();

        // Neither the services nor the task do any work until they are used, so a broken setting can't stop Jellyfin
        // from starting. The task (RenewCertificateTask) is not registered: Jellyfin finds IScheduledTask
        // implementations by scanning plugin assemblies and builds them itself, so a registration would be a second,
        // unused copy.
        serviceCollection.AddSingleton<CertificateServices>();

        // The setup routes' work. It does no I/O until a route is called, for the same reason.
        serviceCollection.AddSingleton<SetupService>();

        // Jellyfin's settings, the undo journal, the restart and setup's progress. Built from factories that only
        // keep references: no file is read or made, and no setting is looked at, until one is used, so installing
        // Trailhead changes nothing (do no harm). A missing Jellyfin service fails the one that needs it, when it is
        // first asked for, and never Jellyfin's start.
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new Journal(() => DataFile(paths, "journal.json"), TimeProvider.System, sp.GetRequiredService<ILogger<Journal>>());
        });
        serviceCollection.AddSingleton(sp => new JellyfinNetwork(
            sp.GetRequiredService<IConfigurationManager>(),
            sp.GetRequiredService<IXmlSerializer>(),
            sp.GetRequiredService<Journal>(),
            sp.GetService<IOptions<ForwardedHeadersOptions>>(),
            sp.GetRequiredService<ILogger<JellyfinNetwork>>()));
        serviceCollection.AddSingleton<IJellyfinRestart, JellyfinRestart>();
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new SetupProgress(() => DataFile(paths, "setup.json"), ConfigGate.Read);
        });

        // Edge, the TLS listener (M2). Its hosted service starts nothing until ApplicationStarted, and then only when
        // setup has a name and a certificate: installed and not set up, nothing is bound or made (do no harm (a)).
        // Jellyfin's services it needs are looked up then, so a missing one stops Edge, never Jellyfin.
        serviceCollection.AddSingleton<EdgeIdentity>();
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new PortTable(() => DataFile(paths, "ports.json"), sp.GetRequiredService<Journal>());
        });
        serviceCollection.AddSingleton<EdgeService>();
        serviceCollection.AddSingleton<IHostedService>(EdgeOrNothing);
    }

    // The host builds every hosted service before it starts any; an exception here would stop Jellyfin's start. So a
    // service Edge needs that isn't there gives a hosted service that does nothing, and says so once.
    private static IHostedService EdgeOrNothing(IServiceProvider sp)
    {
        try
        {
            return sp.GetRequiredService<EdgeService>();
        }
        catch (Exception e)
        {
            sp.GetService<ILoggerFactory>()?.CreateLogger<EdgeService>().LogWarning("Trailhead's listener can't run here ({Error}); Jellyfin carries on without it", e.GetType().Name);
            return new NoEdge();
        }
    }

    private sealed class NoEdge : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // <jellyfin data>/campground/trailhead/<name>. The folder is made here, on first use, not at registration.
    private static string DataFile(IApplicationPaths paths, string name)
    {
        CampgroundPaths.Initialize(paths);
        return Path.Combine(CampgroundPaths.ForPlugin("trailhead"), name);
    }
}

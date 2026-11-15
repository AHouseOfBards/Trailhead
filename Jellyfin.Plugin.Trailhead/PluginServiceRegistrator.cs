using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

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
    }
}

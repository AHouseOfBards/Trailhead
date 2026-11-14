using Campground.Shared.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// Joins Trailhead to Campground when the host starts: registers with the host's lifetime, so everything Trailhead
/// puts in the shared registry is withdrawn on Jellyfin 12's in-process restart, then announces itself.
/// </summary>
/// <remarks>
/// Trailhead injects no scripts, so no startup filter calls <see cref="CampgroundLifetime.Register"/> for it
/// (03-architecture §1, Edge row). The announcement comes after Register, not from the plugin constructor: a record
/// written before Register goes unstamped and outlives the host that wrote it.
/// </remarks>
internal sealed class CampgroundPresence : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CampgroundPresence> _logger;

    public CampgroundPresence(IHostApplicationLifetime lifetime, ILogger<CampgroundPresence> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            CampgroundLifetime.Register(PluginIds.Trailhead, _lifetime);
            PluginRegistry.Announce(
                PluginIds.Trailhead,
                "Trailhead",
                typeof(CampgroundPresence).Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                Array.Empty<string>(),
                () => new PluginStatus(Enabled: true, EmergencyDisabled: false));
        }
        catch (Exception ex)
        {
            // Fail open: without the registry, other plugins see Trailhead as missing and use their own paths.
            _logger.LogWarning(ex, "Trailhead couldn't join Campground; other plugins will treat it as not installed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

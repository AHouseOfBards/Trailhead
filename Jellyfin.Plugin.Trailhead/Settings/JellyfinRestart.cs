using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Settings;

/// <summary>Restarts Jellyfin. Behind an interface so tests never restart anything.</summary>
internal interface IJellyfinRestart
{
    /// <summary>Asks Jellyfin to restart. False when it couldn't be asked; Jellyfin then just keeps running.</summary>
    bool Restart();
}

/// <summary>
/// The dashboard's own Restart: <c>POST /System/Restart</c> calls <c>ISystemManager.Restart()</c>
/// (Jellyfin.Api/Controllers/SystemController.cs:114-122, v12.4), which sets <c>ShouldRestart</c> and stops the host
/// after 100 ms (Emby.Server.Implementations/SystemManager.cs:126-140), and Jellyfin.Server/Program.cs:147-163 starts the server again in the
/// same process. Nothing of Trailhead's own, so there is one way a restart happens.
/// </summary>
/// <remarks>
/// <c>ISystemManager</c> is registered scoped (Emby.Server.Implementations/ApplicationHost.cs:515), so it is taken
/// from a scope of its own rather than injected into this singleton, which the container would refuse with scope
/// validation on. The scope can go at once: the restart runs on a task that holds only singletons.
/// </remarks>
internal sealed class JellyfinRestart : IJellyfinRestart
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JellyfinRestart> _logger;

    public JellyfinRestart(IServiceScopeFactory scopes, ILogger<JellyfinRestart> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public bool Restart()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var system = scope.ServiceProvider.GetService<ISystemManager>();
            if (system is null)
            {
                _logger.LogWarning("Trailhead couldn't ask Jellyfin to restart: no system manager was found");
                return false;
            }

            _logger.LogInformation("Trailhead is restarting Jellyfin so its new network settings take effect");
            system.Restart();
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't ask Jellyfin to restart ({Error})", e.GetType().Name);
            return false;
        }
    }
}

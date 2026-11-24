using Jellyfin.Plugin.Trailhead.Router;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// Keeps Trailhead's router forwards alive, every ten minutes (RASA's <c>portkeep</c>, §3b). A router that won't
/// grant a permanent mapping gives a lease, at most a week on miniupnpd, and without this remote access would stop a
/// week after a perfect setup with nothing to show why. Public and found by Jellyfin's assembly scan, like
/// <see cref="RenewCertificateTask"/>, so it is not registered in DI.
/// </summary>
/// <remarks>Never throws except for Jellyfin cancelling it. With no forwards recorded it does nothing, not even a search.</remarks>
public sealed class RenewForwardsTask : IScheduledTask
{
    private readonly RouterServices _services;
    private readonly ILogger<RenewForwardsTask> _logger;

    // Nothing here but storing: Jellyfin fails the whole plugin when a task can't be built.
    public RenewForwardsTask(RouterServices services, ILogger<RenewForwardsTask> logger)
    {
        _services = services;
        _logger = logger;
    }

    public string Name => "Keep the router forward open";

    public string Key => "TrailheadRenewForwards";

    public string Description => "Checks the port forward Trailhead made on the router and renews it before its lease runs out. Does nothing until Trailhead has made one.";

    public string Category => "Trailhead";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        try
        {
            Log(await _services.RenewAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Trailhead's router check was stopped because Jellyfin is shutting down.");
        }
        catch (Exception e)
        {
            // The type only: router answers and paths can carry anything.
            _logger.LogWarning("Trailhead's router check failed unexpectedly ({Error}). It will try again at the next run.", e.GetType().Name);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(10).Ticks };

        // A router that restarted while Jellyfin was off has lost a leased mapping; don't wait ten minutes to find out.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    private void Log(IReadOnlyList<ForwardReport> reports)
    {
        foreach (var r in reports)
        {
            // Info only when something changed or is wrong: this runs every ten minutes for the life of the server.
            switch (r.Outcome)
            {
                case ForwardOutcome.Ok or ForwardOutcome.Renewed or ForwardOutcome.Dropped:
                    _logger.LogDebug("Router forward for outside port {Port}: {Outcome}.", r.Record.ExternalPort, r.Outcome);
                    break;
                case ForwardOutcome.Readded or ForwardOutcome.Moved:
                    _logger.LogInformation("Router forward for outside port {Port}: {Outcome}.", r.Record.ExternalPort, r.Outcome);
                    break;
                default:
                    if (r.Fault)
                    {
                        _logger.LogWarning("Router forward for outside port {Port}: {Outcome}.", r.Record.ExternalPort, r.Outcome);
                    }
                    else
                    {
                        _logger.LogInformation("Router forward for outside port {Port}: {Outcome}; trying again at the next run.", r.Record.ExternalPort, r.Outcome);
                    }

                    break;
            }
        }
    }
}

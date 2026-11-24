using Jellyfin.Plugin.Trailhead.Health;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// Keeps the name pointed at this connection, every ten minutes (03 §2 Tasks, A3). Found by Jellyfin's assembly scan,
/// like <see cref="RenewForwardsTask"/>. With no name or key saved it does nothing at all, not even a lookup.
/// </summary>
public sealed class AddressSyncTask : IScheduledTask
{
    private readonly HealthServices _services;
    private readonly ILogger<AddressSyncTask> _logger;

    // Nothing here but storing: Jellyfin fails the whole plugin when a task can't be built.
    public AddressSyncTask(HealthServices services, ILogger<AddressSyncTask> logger)
    {
        _services = services;
        _logger = logger;
    }

    public string Name => "Keep the web address up to date";

    public string Key => "TrailheadAddressSync";

    public string Description => "Checks that Trailhead's name on Dynu points at this connection's public address, and updates it when it doesn't. Does nothing until Trailhead is set up.";

    public string Category => "Trailhead";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        try
        {
            var status = await _services.Sync.RunAsync(cancellationToken).ConfigureAwait(false);
            if (status.IsFault(status.At))
            {
                _logger.LogWarning("Trailhead's address check is failing: {Outcome}", status.Outcome);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Trailhead's address check was stopped because Jellyfin is shutting down.");
        }
        catch (Exception e)
        {
            // The type only: Dynu's answers can carry anything.
            _logger.LogWarning("Trailhead's address check failed unexpectedly ({Error}). It will try again at the next run.", e.GetType().Name);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(10).Ticks };

        // An address that changed while Jellyfin was off shouldn't wait ten minutes.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}

/// <summary>
/// Checks every part of remote access, every ten minutes, and writes an activity-log entry when a problem starts, each
/// day it lasts, and when it clears (T23). Found by Jellyfin's assembly scan. Not set up, it finds nothing to report.
/// </summary>
public sealed class HealthTask : IScheduledTask
{
    private readonly HealthServices _services;
    private readonly ILogger<HealthTask> _logger;

    public HealthTask(HealthServices services, ILogger<HealthTask> logger)
    {
        _services = services;
        _logger = logger;
    }

    public string Name => "Check remote access";

    public string Key => "TrailheadHealth";

    public string Description => "Checks the certificate Trailhead serves, its listener, Jellyfin's network settings, the router forward and the web address, and notes problems in the activity log.";

    public string Category => "Trailhead";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        try
        {
            var report = await _services.Health.RunAsync(cancellationToken).ConfigureAwait(false);
            if (report.Overall == Overall.Problem)
            {
                _logger.LogInformation("Trailhead's health check found a problem: {Checks}", string.Join(", ", report.Checks.Where(c => c.State == CheckState.Problem).Select(c => c.Id + ":" + c.Code)));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Trailhead's health check was stopped because Jellyfin is shutting down.");
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's health check failed unexpectedly ({Error}). It will try again at the next run.", e.GetType().Name);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(10).Ticks };
    }
}

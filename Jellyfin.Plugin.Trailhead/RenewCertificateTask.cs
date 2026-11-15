using Jellyfin.Plugin.Trailhead.Acme;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// The daily certificate check, visible and runnable from Jellyfin's dashboard. It is public and found by Jellyfin's
/// assembly scan (ApplicationHost.GetExports builds it with the service provider), so it is not registered in DI.
/// </summary>
/// <remarks>
/// It never throws, except for Jellyfin cancelling it: a red task is noise, and the next trigger is the retry.
/// It doesn't loop or sleep to retry for the same reason.
/// </remarks>
public sealed class RenewCertificateTask : IScheduledTask
{
    private readonly CertificateServices _services;
    private readonly ILogger<RenewCertificateTask> _logger;

    // Nothing here but storing: Jellyfin fails the whole plugin when a task can't be built.
    public RenewCertificateTask(CertificateServices services, ILogger<RenewCertificateTask> logger)
    {
        _services = services;
        _logger = logger;
    }

    public string Name => "Renew the HTTPS certificate";

    public string Key => "TrailheadRenewCertificate";

    public string Description => "Checks Trailhead's HTTPS certificate and gets a new one when it is due. Does nothing until Trailhead is set up.";

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
            _logger.LogInformation("Trailhead's certificate check was stopped because Jellyfin is shutting down.");
        }
        catch (Exception e)
        {
            // The type only: an exception's text can carry anything, a key included.
            _logger.LogWarning("Trailhead's certificate check failed unexpectedly ({Error}). It will try again at the next run.", e.GetType().Name);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Not on the hour: the CA's own clients pile up there.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = new TimeSpan(3, 17, 0).Ticks };

        // Jellyfin fires this three seconds after it starts, so a server that was off for days catches up.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    private void Log(RenewalReport report)
    {
        switch (report.State)
        {
            case RenewalState.NotSetUp:
                _logger.LogInformation("{Message}", report.Message);
                return;
            case RenewalState.BuildFailed:
                _logger.LogWarning("{Message} It will try again at the next run.", report.Message);
                return;
        }

        var result = report.Result!;
        switch (result.Outcome)
        {
            case ReconcileOutcome.UpToDate:
                _logger.LogInformation("Trailhead's certificate is up to date. Next check: {Next:u}.", result.NextCheck);
                break;
            case ReconcileOutcome.Issued:
                _logger.LogInformation("Trailhead got a new certificate. Next check: {Next:u}.", result.NextCheck);
                break;
            case ReconcileOutcome.Blocked:
                _logger.LogWarning("Trailhead is holding off after failed validations and will try again after {Next:u}.", result.NextCheck);
                break;
            default:
                // A permanent failure has no retry time; the next daily run asks again anyway, and fails the same way until something changes.
                _logger.LogWarning("Trailhead could not renew its certificate ({Kind}): {Message}", result.Kind, result.Message);
                break;
        }
    }
}

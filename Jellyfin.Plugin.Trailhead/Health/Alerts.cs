using Campground.Shared.Storage;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>Which kind of alert went out.</summary>
internal enum AlertKind
{
    None,

    /// <summary>Something new is failing, or something different from what was reported.</summary>
    Started,

    /// <summary>The same problem, a day after it was last reported.</summary>
    Still,

    /// <summary>Everything reported earlier has cleared.</summary>
    Cleared,
}

/// <summary>One fault that has outlasted its grace window. <paramref name="Key"/> names what is failing and how.</summary>
internal sealed record Fault(string Key, string Text);

/// <summary>Where alerts go.</summary>
internal interface IAlertSink
{
    Task RaiseAsync(AlertKind kind, string name, string overview, CancellationToken ct);
}

/// <summary>
/// Jellyfin's activity log, which the dashboard's home page shows (T23). <c>IActivityManager.CreateAsync</c>
/// (MediaBrowser.Model/Activity/IActivityManager.cs, v12.4) with an <c>ActivityLog</c> whose name and overviews are at
/// most 512 characters (src/Jellyfin.Database/Jellyfin.Database.Implementations/Entities/ActivityLog.cs:45-67).
/// </summary>
internal sealed class ActivityLogAlerts : IAlertSink
{
    internal const string EntryType = "Trailhead";
    private const int MaxText = 512;

    private readonly Func<IActivityManager?> _activity;

    internal ActivityLogAlerts(Func<IActivityManager?> activity) => _activity = activity;

    public async Task RaiseAsync(AlertKind kind, string name, string overview, CancellationToken ct)
    {
        var manager = _activity() ?? throw new InvalidOperationException("Jellyfin's activity log isn't available.");
        await manager.CreateAsync(new ActivityLog(Cut(name), EntryType, Guid.Empty)
        {
            ShortOverview = Cut(overview),
            Overview = Cut(overview),
            LogSeverity = kind == AlertKind.Cleared ? LogLevel.Information : LogLevel.Warning,
        }).ConfigureAwait(false);
    }

    internal static string Cut(string text) => text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";
}

/// <summary>What the escalator last reported. Kept beside the health file.</summary>
internal sealed record AlertMemory(string Signature, DateTimeOffset Raised);

/// <summary>
/// Decides when a problem is worth an activity-log entry (T23, RASA <c>health/escalate</c>): when what is failing
/// changes, once a day while it lasts, and once when it clears, only if it was reported. A server that has always been
/// fine never writes a word; the health file is where "fine" is recorded. Grace windows are applied before this: only
/// faults reach it.
/// </summary>
internal sealed class Escalator
{
    /// <summary>A fault still there this long after its last report is reported again. Every ten minutes would bury the log.</summary>
    internal static readonly TimeSpan RepeatAfter = TimeSpan.FromHours(24);

    private const string Ok = "ok";

    private readonly AtomicJsonFile _file;
    private readonly IAlertSink _sink;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    internal Escalator(string statePath, IAlertSink sink, TimeProvider time, ILogger logger)
    {
        _file = new AtomicJsonFile(statePath);
        _sink = sink;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raises an alert if this run's faults call for one. Never throws except for <paramref name="ct"/>.</summary>
    internal async Task<AlertKind> ConsiderAsync(IReadOnlyList<Fault> faults, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var last = Load();
        var signature = faults.Count == 0 ? Ok : string.Join(",", faults.Select(f => f.Key).Distinct().Order(StringComparer.Ordinal));

        AlertKind kind;
        if (signature == Ok)
        {
            // Owed only to someone who was told there was a problem.
            if (last is null || last.Signature == Ok)
            {
                return AlertKind.None;
            }

            kind = AlertKind.Cleared;
        }
        else if (last is null || last.Signature != signature)
        {
            kind = AlertKind.Started;
        }
        else if (now - last.Raised >= RepeatAfter)
        {
            kind = AlertKind.Still;
        }
        else
        {
            return AlertKind.None;
        }

        var name = UiText.English(kind switch
        {
            AlertKind.Cleared => "alert.cleared",
            AlertKind.Still => "alert.still",
            _ => "alert.started",
        });
        var overview = kind == AlertKind.Cleared ? UiText.English("alert.clearedDetail") : string.Join(" ", faults.Select(f => f.Text).Distinct());
        try
        {
            await _sink.RaiseAsync(kind, name, overview, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't write to Jellyfin's activity log ({Error})", e.GetType().Name);
        }

        // Recorded whether or not the log took it (RASA): otherwise a log that refuses would be retried every ten
        // minutes for ever, the noise this exists to prevent. The health file still has the problem.
        try
        {
            _file.Save(new AlertMemory(signature, now));
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't save what it last reported ({Error})", e.GetType().Name);
        }

        return kind;
    }

    // A missing or damaged file reads as "nothing reported yet". At worst that repeats one alert; the other way round
    // a damaged file would silence a real fault for ever (RASA health/escalate.go).
    private AlertMemory? Load()
    {
        try
        {
            return _file.Load<AlertMemory>() is { Signature.Length: > 0 } m ? m : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

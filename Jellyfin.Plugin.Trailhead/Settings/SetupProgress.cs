using System.Text.Json;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Configuration;

namespace Jellyfin.Plugin.Trailhead.Settings;

/// <summary>The setup steps, in order (03 §3). The names are stored, so don't rename one.</summary>
internal enum SetupStep
{
    Key,
    Name,
    AdminWarning,
    Certificate,
    JellyfinSettings,
    Restart,
    Gate,
    Router,
    OutsideCheck,
    Done,
}

/// <summary>Answers the admin gave, so a continued setup doesn't ask again (T39).</summary>
internal sealed class SetupAnswers
{
    /// <summary>The home subnet the admin confirmed for Docker (03 §2b), or null when none was asked or given.</summary>
    public string? DockerSubnet { get; set; }

    /// <summary>The admin chose "Finish the router later" (T38).</summary>
    public bool FinishRouterLater { get; set; }
}

internal sealed class SetupRecord
{
    public List<string> Done { get; set; } = new();

    public SetupAnswers Answers { get; set; } = new();

    /// <summary>
    /// Set when setup asks Jellyfin to restart: the id of the running copy of this service. A different copy reading
    /// it later means the restart has happened.
    /// </summary>
    public string? RestartAskedBy { get; set; }
}

/// <summary>Where setup is: the steps done, the step to continue at, and the answers given.</summary>
internal sealed record SetupPosition(IReadOnlySet<SetupStep> Done, SetupStep Next, SetupStep? LastSafe, SetupAnswers Answers);

/// <summary>
/// A small record of how far setup got (T39), so it continues from its last safe point after a closed page, a
/// restart or a crash. Each step is idempotent and does its own checking; this only says where to pick up. The record
/// is <c>…/campground/trailhead/setup.json</c>, made on the first step that's recorded, never at startup.
/// </summary>
/// <remarks>
/// The Dynu key and the name were saved before this record existed, so those two are read from the configuration as
/// well: an install that had them before this version continues at the step after.
/// </remarks>
internal sealed class SetupProgress
{
    private static readonly object Gate = new();

    private readonly Func<string> _path;
    private readonly Func<PluginConfiguration> _config;

    // One per copy of the service. Jellyfin's in-process restart builds new singletons, so a new id means a new start.
    private readonly string _instance = Guid.NewGuid().ToString("N");

    internal SetupProgress(Func<string> path, Func<PluginConfiguration> config)
    {
        _path = path;
        _config = config;
    }

    internal SetupPosition Read()
    {
        lock (Gate)
        {
            return Position(Load());
        }
    }

    /// <summary>Marks a step done. Marking it again changes nothing.</summary>
    internal SetupPosition MarkDone(SetupStep step)
    {
        lock (Gate)
        {
            var record = Load();
            if (!record.Done.Contains(step.ToString()))
            {
                record.Done.Add(step.ToString());
                Save(record);
            }

            return Position(record);
        }
    }

    /// <summary>Records that Jellyfin is about to restart for setup. The Restart step counts as done once a new start reads this.</summary>
    internal void RestartAsked()
    {
        lock (Gate)
        {
            var record = Load();
            record.RestartAskedBy = _instance;
            Save(record);
        }
    }

    internal SetupPosition Answer(Action<SetupAnswers> change)
    {
        lock (Gate)
        {
            var record = Load();
            change(record.Answers);
            Save(record);
            return Position(record);
        }
    }

    /// <summary>Forgets the whole record (after Remove).</summary>
    internal void Clear()
    {
        lock (Gate)
        {
            new AtomicJsonFile(_path()).Delete();
        }
    }

    private SetupPosition Position(SetupRecord record)
    {
        var done = new HashSet<SetupStep>();
        foreach (var name in record.Done)
        {
            // A step name from a newer Trailhead is kept in the file and ignored here.
            if (Enum.TryParse<SetupStep>(name, out var step) && Enum.IsDefined(step))
            {
                done.Add(step);
            }
        }

        PluginConfiguration? config = null;
        try
        {
            config = _config();
        }
        catch (Exception)
        {
            // No configuration to read: the record alone says where setup is.
        }

        if (!string.IsNullOrWhiteSpace(config?.DynuKey))
        {
            done.Add(SetupStep.Key);
        }

        if (!string.IsNullOrWhiteSpace(config?.Hostname))
        {
            done.Add(SetupStep.Name);
        }

        if (record.RestartAskedBy is { } asker && asker != _instance)
        {
            done.Add(SetupStep.Restart);
        }

        // Continue at the first step not done. The last safe point is the step before it: everything up to there is done.
        var next = Enum.GetValues<SetupStep>().FirstOrDefault(s => !done.Contains(s), SetupStep.Done);
        SetupStep? lastSafe = next == SetupStep.Key ? null : next - 1;
        return new SetupPosition(done, next, lastSafe, Copy(record.Answers));
    }

    private static SetupAnswers Copy(SetupAnswers a) => new() { DockerSubnet = a.DockerSubnet, FinishRouterLater = a.FinishRouterLater };

    private SetupRecord Load()
    {
        var file = new AtomicJsonFile(_path());
        try
        {
            return file.Load<SetupRecord>() ?? new SetupRecord();
        }
        catch (JsonException)
        {
            // Unreadable progress costs nothing but a question asked again: every step checks for itself.
            return new SetupRecord();
        }
    }

    private void Save(SetupRecord record) => new AtomicJsonFile(_path()).Save(record);
}

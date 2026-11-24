using Campground.Shared.Storage;

namespace Jellyfin.Plugin.Trailhead.Router;

internal static class ForwardStates
{
    /// <summary>Written before the router is asked. If Jellyfin stops mid-way, this is how the next run finds it.</summary>
    internal const string Pending = "pending";

    /// <summary>The router said yes and the read-back (or a later renewal) matched.</summary>
    internal const string Active = "active";
}

/// <summary>
/// One router forward Trailhead made, or is about to make. Only forwards in this list are ever renewed or deleted
/// (review S10); a mapping on the router that isn't here is never touched.
/// </summary>
/// <param name="InternalClient">The address the forward was pointed at, written before the router is asked.</param>
/// <param name="LeaseSeconds">The lease the router granted at the last add (read back), 0 for permanent.</param>
/// <param name="AddedAt">When the router last accepted an add for it: the start of the lease it is counting down.</param>
/// <param name="ConfirmedAt">When a read-back last showed it in place and pointing here.</param>
internal sealed record ForwardRecord(
    string Id,
    string Owner,
    string Protocol,
    int ExternalPort,
    int InternalPort,
    string InternalClient,
    string State,
    int LeaseSeconds,
    DateTimeOffset RecordedAt,
    DateTimeOffset? AddedAt,
    DateTimeOffset? ConfirmedAt);

internal sealed class ForwardsFile
{
    public List<ForwardRecord> Forwards { get; set; } = new();
}

/// <summary>The recorded-forwards list, on disk with <see cref="AtomicJsonFile"/> so it is never half-written.</summary>
internal sealed class ForwardsStore
{
    private readonly AtomicJsonFile _file;

    internal ForwardsStore(string path) => _file = new AtomicJsonFile(path);

    internal string FilePath => _file.FilePath;

    internal IReadOnlyList<ForwardRecord> Load() => _file.Load<ForwardsFile>()?.Forwards ?? new List<ForwardRecord>();

    internal void Put(ForwardRecord record)
    {
        var all = Load().Where(r => r.Id != record.Id).ToList();
        all.Add(record);
        _file.Save(new ForwardsFile { Forwards = all });
    }

    internal void Remove(string id)
    {
        var all = Load().ToList();
        if (all.RemoveAll(r => r.Id == id) > 0)
        {
            _file.Save(new ForwardsFile { Forwards = all });
        }
    }
}

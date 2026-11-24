using System.Text.Json;
using Campground.Shared.Registry;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Settings;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>One port Trailhead holds.</summary>
internal sealed class PortReservation
{
    public string Owner { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public string Protocol { get; set; } = "tcp";

    public int Port { get; set; }
}

internal sealed class PortTableDocument
{
    public List<PortReservation> Reservations { get; set; } = new();
}

/// <summary>
/// The one reservation table for ports (03 §3b, T26): <c>…/campground/trailhead/ports.json</c>. Edge's listener, the
/// router mapping, the published URL and the status page all read Edge's port from here, so there is one source of
/// truth (RASA kept it in three places and shipped the wrong one, review B1). Later, <c>ports.v1</c> reservations go
/// in the same table. Made the first time Edge binds, never at install.
/// </summary>
internal sealed class PortTable
{
    internal const string EdgeOwner = PluginIds.Trailhead;
    internal const string EdgePurpose = "edge";

    /// <summary>The journal key for Edge's reservation.</summary>
    internal const string EdgeJournalKey = "ports.trailhead.edge.tcp";

    // Static for the same reason as the journal's: an old copy left by an in-process restart writes the same file.
    private static readonly object Gate = new();

    private readonly Func<string> _path;
    private readonly Journal _journal;

    internal PortTable(Func<string> path, Journal journal)
    {
        _path = path;
        _journal = journal;
    }

    /// <summary>The port Edge holds, or null when it has none yet. A table that can't be read counts as none.</summary>
    internal int? EdgePort()
    {
        lock (Gate)
        {
            return Load()?.Reservations.FirstOrDefault(IsEdge)?.Port;
        }
    }

    internal IReadOnlyList<PortReservation> All()
    {
        lock (Gate)
        {
            return Load()?.Reservations.ToList() ?? new List<PortReservation>();
        }
    }

    /// <summary>
    /// Records Edge's port: in the undo journal first, then in the table, as one step. Nothing happens when it is
    /// already the port held. Throws when the journal can't take it (<see cref="JournalUnavailableException"/>) or the
    /// table can't be written; the caller then doesn't serve on that port, so the listener and the table never differ.
    /// </summary>
    internal void ReserveEdge(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        lock (Gate)
        {
            var doc = Load() ?? new PortTableDocument();
            var current = doc.Reservations.FirstOrDefault(IsEdge);
            if (current?.Port == port)
            {
                return;
            }

            JsonElement? before = current is null ? null : JsonSerializer.SerializeToElement(current.Port);
            _journal.Record(JournalKinds.PortReservation, EdgeJournalKey, before, JsonSerializer.SerializeToElement(port));

            doc.Reservations.RemoveAll(IsEdge);
            doc.Reservations.Add(new PortReservation { Owner = EdgeOwner, Purpose = EdgePurpose, Protocol = "tcp", Port = port });
            new AtomicJsonFile(_path()).Save(doc);
        }
    }

    private static bool IsEdge(PortReservation r)
        => r.Owner == EdgeOwner && r.Purpose == EdgePurpose && string.Equals(r.Protocol, "tcp", StringComparison.OrdinalIgnoreCase);

    private PortTableDocument? Load()
    {
        var path = _path();
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            return null;
        }

        try
        {
            var doc = new AtomicJsonFile(path).Load<PortTableDocument>();
            return doc?.Reservations is null ? null : doc;
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException)
        {
            return null;
        }
    }
}

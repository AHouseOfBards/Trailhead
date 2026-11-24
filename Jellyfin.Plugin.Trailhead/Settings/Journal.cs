using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Campground.Shared.Registry;
using Campground.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Settings;

/// <summary>
/// What a journal entry records. Open on purpose: later milestones add their own kinds, and a journal written by a
/// newer Trailhead may hold a kind this one doesn't know. Such an entry is kept and left alone, never dropped.
/// </summary>
internal static class JournalKinds
{
    internal const string JellyfinSetting = "JellyfinSetting";
    internal const string RouterMapping = "RouterMapping";
    internal const string FirewallRule = "FirewallRule";
    internal const string DnsName = "DnsName";

    /// <summary>A port in Trailhead's reservation table (Edge's listener). Before and written are port numbers.</summary>
    internal const string PortReservation = "PortReservation";
}

/// <summary>
/// One change Trailhead made outside itself: the value before (what Remove puts back) and the value Trailhead wrote
/// (what drift is measured against). Values are JSON, so each kind can hold what it needs.
/// </summary>
internal sealed class JournalEntry
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public JsonElement? Before { get; set; }

    public JsonElement? Written { get; set; }

    /// <summary>When the "before" was taken: the first time Trailhead changed this key.</summary>
    public DateTimeOffset FirstAt { get; set; }

    /// <summary>When Trailhead last wrote the key.</summary>
    public DateTimeOffset At { get; set; }

    internal JournalEntry Copy() => (JournalEntry)MemberwiseClone();
}

internal sealed class JournalDocument
{
    public List<JournalEntry> Entries { get; set; } = new();

    /// <summary>
    /// The name of the unreadable journal that was kept aside, until an admin has seen it. Saved in the journal itself
    /// so a restart can't forget it.
    /// </summary>
    public string? DamagedFile { get; set; }
}

/// <summary>The journal can't take a new change: it was found unreadable and nobody has looked yet, or it can't be read now.</summary>
internal sealed class JournalUnavailableException : Exception
{
    internal JournalUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The undo journal (T11): <c>…/campground/trailhead/journal.json</c>. Every change Trailhead makes outside itself is
/// recorded here, written and flushed to disk, <b>before</b> the change is made, so a crash between the two leaves an
/// entry whose "before" still restores what the admin had.
/// </summary>
/// <remarks>
/// It does nothing until it is first used: installing Trailhead must not create files (do no harm). Shared
/// <see cref="AtomicJsonFile"/> falls back to its <c>.bak</c> without a word when the main file is corrupt; that is
/// right for a cache but not here, where a silently older journal would make Remove put back the wrong values. So the
/// main file is parsed here first, and a bad one is moved aside and named, and new changes are refused until the
/// admin has been told (<see cref="AcknowledgeDamage"/>).
/// </remarks>
internal sealed class Journal
{
    // One lock for every read-modify-write. Static: two instances on one file (a test, or an old service a restart
    // left behind) must still take turns.
    private static readonly object Gate = new();

    private readonly Func<string> _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    internal Journal(Func<string> path, TimeProvider time, ILogger logger)
    {
        _path = path;
        _time = time;
        _logger = logger;
    }

    /// <summary>The journal's file, worked out when first needed.</summary>
    internal string FilePath => _path();

    /// <summary>The name of the journal kept aside because it couldn't be read, or null when there is none to report.</summary>
    internal string? DamagedFile
    {
        get
        {
            lock (Gate)
            {
                return Load().DamagedFile;
            }
        }
    }

    /// <summary>A copy of every entry, oldest first.</summary>
    internal IReadOnlyList<JournalEntry> Entries()
    {
        lock (Gate)
        {
            return Load().Entries.Select(e => e.Copy()).ToList();
        }
    }

    internal JournalEntry? Find(string kind, string key)
    {
        lock (Gate)
        {
            return Load().Entries.FirstOrDefault(e => e.Kind == kind && e.Key == key)?.Copy();
        }
    }

    /// <summary>
    /// Records a change that is about to be made, and returns once it is on disk. Recording a key that is already
    /// there keeps the first "before": Remove should put back what the admin had, not a value Trailhead wrote on the
    /// way. Throws <see cref="JournalUnavailableException"/> rather than let a change go unrecorded.
    /// </summary>
    internal JournalEntry Record(string kind, string key, JsonElement? before, JsonElement? written)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (Gate)
        {
            var doc = Load();
            if (doc.DamagedFile is not null)
            {
                // Without the entries in the bad file, a fresh "before" here could be a value Trailhead itself wrote.
                throw new JournalUnavailableException("The undo journal was unreadable and has not been looked at yet.");
            }

            var now = _time.GetUtcNow();
            var entry = doc.Entries.FirstOrDefault(e => e.Kind == kind && e.Key == key);
            var next = Clone(doc);
            if (entry is null)
            {
                entry = new JournalEntry { Id = Guid.NewGuid().ToString("N"), Kind = kind, Key = key, Before = before, FirstAt = now };
                next.Entries.Add(entry);
            }
            else
            {
                entry = next.Entries.First(e => e.Id == entry.Id);
            }

            entry.Written = written;
            entry.At = now;
            Save(next);
            return entry.Copy();
        }
    }

    /// <summary>Drops entries once what they record has been undone. Unknown ids are ignored.</summary>
    internal void Remove(IEnumerable<string> ids)
    {
        var drop = ids.ToHashSet(StringComparer.Ordinal);
        lock (Gate)
        {
            var next = Clone(Load());
            if (next.Entries.RemoveAll(e => drop.Contains(e.Id)) > 0)
            {
                Save(next);
            }
        }
    }

    /// <summary>The admin has been told about the bad journal: new changes may be recorded again. The bad file stays.</summary>
    internal void AcknowledgeDamage()
    {
        lock (Gate)
        {
            var next = Clone(Load());
            if (next.DamagedFile is null)
            {
                return;
            }

            next.DamagedFile = null;
            Save(next);
        }
    }

    // A copy to change, so a save that fails leaves the journal in memory as it is on disk.
    private static JournalDocument Clone(JournalDocument doc)
        => new() { DamagedFile = doc.DamagedFile, Entries = doc.Entries.Select(e => e.Copy()).ToList() };

    private void Save(JournalDocument next)
    {
        new AtomicJsonFile(FilePath).Save(next);
    }

    // Read from disk every time, under the lock. A copy kept in memory went stale as soon as a second instance (an
    // old service left over from an in-process restart) wrote the same file.
    private JournalDocument Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            // AtomicJsonFile may still find a .bak (a crash between its two renames): that is the journal, not damage.
            return TryParse(path + ".bak") ?? new JournalDocument();
        }

        // An IOException here (the file is held by someone) is not damage: it goes up, and the next call tries again.
        var parsed = TryParse(path);
        if (parsed is not null)
        {
            return parsed;
        }

        var aside = Path.Combine(Path.GetDirectoryName(path)!, "journal.bad-" + _time.GetUtcNow().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".json");
        File.Move(path, aside);
        _logger.LogWarning("Trailhead's undo journal couldn't be read. It was kept as {File}, and Trailhead makes no new changes until that has been looked at", Path.GetFileName(aside));

        // The backup is one save older at most. It is used, but the damage is still reported.
        var recovered = TryParse(path + ".bak") ?? new JournalDocument();
        recovered.DamagedFile = Path.GetFileName(aside);
        new AtomicJsonFile(path).Save(recovered);
        return recovered;
    }

    // The document in a file, or null when it isn't a readable journal. Missing files are null too.
    private static JournalDocument? TryParse(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > AtomicJsonFile.DefaultMaxBytes)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node)
            {
                return null;
            }

            var doc = node.Deserialize<JournalDocument>(CampgroundRegistry.Json);
            if (doc?.Entries is null || doc.Entries.Any(e => e is null || string.IsNullOrEmpty(e.Id) || string.IsNullOrEmpty(e.Kind) || string.IsNullOrEmpty(e.Key)))
            {
                return null;
            }

            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

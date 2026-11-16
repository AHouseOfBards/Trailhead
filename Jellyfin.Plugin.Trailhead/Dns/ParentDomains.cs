using System.Text.Json;

namespace Jellyfin.Plugin.Trailhead.Dns;

internal sealed record ParentDomain(string Name, int Rank, bool IsDefault);

/// <summary>The parent domains Trailhead offers (and the ones it refuses), loaded from the embedded catalogue.</summary>
internal sealed class ParentDomains
{
    private const string ResourceName = "parent-domains.json";

    private ParentDomains(IReadOnlyList<ParentDomain> offered, IReadOnlyDictionary<string, string> blocked)
    {
        Offered = offered;
        Blocked = blocked;
    }

    /// <summary>The offered parents in display order.</summary>
    internal IReadOnlyList<ParentDomain> Offered { get; }

    /// <summary>Refused parents and why.</summary>
    internal IReadOnlyDictionary<string, string> Blocked { get; }

    internal ParentDomain Default => Offered.Single(p => p.IsDefault);

    internal static ParentDomains Embedded()
    {
        using var stream = typeof(ParentDomains).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The parent domain list is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Loads a catalogue. A bad one is refused outright rather than half-used: a picker with no default, two
    /// defaults, or a parent that is both offered and blocked is a mistake in the data, so it fails at load.
    /// </summary>
    internal static ParentDomains Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("domains", out var domains) || domains.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("The parent domain list has no \"domains\".");
            }

            var offered = new List<ParentDomain>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in domains.EnumerateArray())
            {
                var name = Normalize(d.ValueKind == JsonValueKind.Object && d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
                if (name.Length == 0)
                {
                    throw new InvalidDataException("The parent domain list has an entry with no name.");
                }

                if (!seen.Add(name))
                {
                    throw new InvalidDataException($"The parent domain list has {name} twice.");
                }

                var rank = d.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var rankValue) ? rankValue : 0;
                var isDefault = d.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.True;
                offered.Add(new ParentDomain(name, rank, isDefault));
            }

            if (offered.Count == 0)
            {
                throw new InvalidDataException("The parent domain list is empty.");
            }

            var defaults = offered.Count(p => p.IsDefault);
            if (defaults != 1)
            {
                throw new InvalidDataException($"The parent domain list must mark exactly one default, not {defaults}.");
            }

            var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("blocked", out var blockedArray) && blockedArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in blockedArray.EnumerateArray())
                {
                    var name = Normalize(b.ValueKind == JsonValueKind.Object && b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);
                    var reason = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
                    if (name.Length == 0 || string.IsNullOrWhiteSpace(reason))
                    {
                        throw new InvalidDataException("A blocked parent needs a name and a reason.");
                    }

                    if (seen.Contains(name))
                    {
                        throw new InvalidDataException($"The parent domain list both offers and blocks {name}.");
                    }

                    blocked[name] = reason;
                }
            }

            return new ParentDomains(offered.OrderBy(p => p.Rank).ToList(), blocked);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The parent domain list is not valid JSON.", e);
        }
    }

    internal bool Allows(string? parent)
    {
        var name = Normalize(parent);
        return Offered.Any(p => p.Name == name);
    }

    internal string? BlockedReason(string? parent) => Blocked.TryGetValue(Normalize(parent), out var reason) ? reason : null;

    /// <summary>Every offered parent except the one given, in display order. A taken label is offered under these.</summary>
    internal IReadOnlyList<ParentDomain> Others(string parent)
    {
        var name = Normalize(parent);
        return Offered.Where(p => p.Name != name).ToList();
    }

    internal static string Normalize(string? name) => (name ?? string.Empty).Trim().Trim('.').ToLowerInvariant();
}

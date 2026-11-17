using System.Globalization;
using Campground.Shared.I18n;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// Every sentence a person reads comes out of Web/i18n/*.json, embedded as <c>Trailhead.i18n.&lt;lang&gt;.json</c>.
/// C# code returns kinds, problems and codes; text is looked up by a stable key.
/// </summary>
internal static class UiText
{
    internal const string Prefix = "Trailhead.i18n";

    // PublicationOnly: a failed load throws every time it is asked, not once and then for good.
    private static readonly Lazy<Strings> Loaded = new(() => Strings.Load(typeof(UiText).Assembly, Prefix), LazyThreadSafetyMode.PublicationOnly);

    // A header or a path segment comes from outside; only this much of it is looked at.
    private const int MaxHeader = 512;
    private const int MaxTags = 16;

    internal static Strings Table => Loaded.Value;

    internal static string English(string key, IReadOnlyDictionary<string, object?>? args = null) => Table.Get(key, Strings.Fallback, args);

    /// <summary>
    /// The language that exists for a requested one, or English. A tag is cut back one subtag at a time
    /// (pt-BR-x-foo, pt-BR, pt), so a caller can ask for anything and only ever reach a table that is there.
    /// </summary>
    internal static string Resolve(Strings strings, string? requested)
        => Match(strings, requested) ?? Strings.Fallback;

    /// <summary>The best language for an Accept-Language header, by q value, or English.</summary>
    internal static string FromAcceptLanguage(Strings strings, string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Strings.Fallback;
        }

        var text = header.Length > MaxHeader ? header[..MaxHeader] : header;
        var ranked = new List<(string Tag, double Q, int Order)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxTags))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            var q = 1.0;
            foreach (var p in pieces.Skip(1))
            {
                if (p.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(p[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    q = parsed;
                }
            }

            if (q > 0 && pieces[0] != "*")
            {
                ranked.Add((pieces[0], q, ranked.Count));
            }
        }

        foreach (var (tag, _, _) in ranked.OrderByDescending(r => r.Q).ThenBy(r => r.Order))
        {
            if (Match(strings, tag) is { } found)
            {
                return found;
            }
        }

        return Strings.Fallback;
    }

    private static string? Match(Strings strings, string? requested)
    {
        var tag = (requested ?? string.Empty).Trim();
        while (tag.Length > 0)
        {
            var found = strings.Languages.FirstOrDefault(l => l.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found;
            }

            var dash = tag.LastIndexOf('-');
            tag = dash > 0 ? tag[..dash] : string.Empty;
        }

        return null;
    }
}

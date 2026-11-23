using System.Net;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Trailhead.Settings;

/// <summary>The journal keys of the Jellyfin settings Trailhead writes (03 §2b). Nothing else in network.xml is touched.</summary>
internal static class NetworkKeys
{
    internal const string KnownProxies = "network.KnownProxies";
    internal const string PublishedServerUri = "network.PublishedServerUriBySubnet";
    internal const string EnableRemoteAccess = "network.EnableRemoteAccess";
    internal const string LocalNetworkSubnets = "network.LocalNetworkSubnets";

    /// <summary>In the order setup writes them: trust first, so the published URL never points at an untrusted path.</summary>
    internal static readonly IReadOnlyList<string> All = new[] { KnownProxies, PublishedServerUri, EnableRemoteAccess, LocalNetworkSubnets };
}

/// <summary>
/// What setup wants Jellyfin to know. <paramref name="LocalSubnet"/> is set only for the Docker case, once the admin
/// has confirmed it (M3 detects it).
/// </summary>
internal sealed record NetworkPlan(IPAddress ForwardingAddress, string Hostname, int ExternalPort = 443, string? LocalSubnet = null);

internal enum SettingOutcome
{
    /// <summary>Jellyfin already had the value. Nothing written, nothing journalled.</summary>
    AlreadySet,

    /// <summary>Journalled, written, saved and read back the same.</summary>
    Written,

    /// <summary>The admin has a value of their own in the way. It is kept, and nothing was written.</summary>
    Conflict,

    /// <summary>Not needed or not allowed here (no Docker subnet, or the admin's list isn't empty).</summary>
    Skipped,

    /// <summary>Not done, or not proven done. See <see cref="SettingResult.Problem"/>.</summary>
    Failed,
}

/// <summary>Why a setting wasn't written. Codes, not text: the page looks the words up.</summary>
internal static class SettingProblems
{
    internal const string AdminPublishedUrl = "admin_published_url";
    internal const string ListNotEmpty = "list_not_empty";
    internal const string NotRequested = "not_requested";
    internal const string InvalidPlan = "invalid_plan";
    internal const string BaseUrlUnusable = "base_url_unusable";
    internal const string JournalUnavailable = "journal_unavailable";
    internal const string SaveFailed = "save_failed";
    internal const string ReadBackDiffers = "read_back_differs";
    internal const string UnknownSetting = "unknown_setting";
}

internal sealed record SettingResult(string Key, SettingOutcome Outcome, string? Problem = null);

internal enum DriftState
{
    /// <summary>Saved as Trailhead wrote it, and in effect.</summary>
    InEffect,

    /// <summary>Someone changed it since; <see cref="DriftItem.Now"/> has the value now.</summary>
    Changed,

    /// <summary>Saved as written, but Jellyfin is still running on the old value until it restarts (Known proxies).</summary>
    SavedNotInEffect,

    /// <summary>Couldn't be read. Not "fine", and not "changed" either.</summary>
    Unknown,
}

internal sealed record DriftItem(string Id, string Key, DriftState State, JsonElement? Now);

internal enum RestoreOutcome
{
    /// <summary>The "before" value is back, read back the same, and the entry is gone.</summary>
    Restored,

    /// <summary>It already had the "before" value (a change that never landed). The entry is gone.</summary>
    AlreadyBefore,

    /// <summary>The admin changed it since Trailhead wrote it, so it was left as it is. The entry stays.</summary>
    ChangedSince,

    /// <summary>The write or the read-back failed. The entry stays.</summary>
    Failed,

    /// <summary>Not a Jellyfin setting this version knows. The entry stays.</summary>
    NotMine,
}

internal sealed record RestoreResult(string Id, string Key, RestoreOutcome Outcome, string? Problem = null);

/// <summary>
/// Writes Jellyfin's network settings through its own typed configuration (<c>GetNetworkConfiguration()</c> and
/// <c>SaveConfiguration("network", …)</c>, the path the dashboard's Networking page and the startup wizard take:
/// Jellyfin.Api/Controllers/StartupController.cs:97-101, v12.4). Every change is journalled before it is made, then
/// saved, then read back from network.xml itself, not from Jellyfin's cache, and fails if the file says otherwise.
/// </summary>
/// <remarks>
/// It changes a copy of the configuration and saves the copy. Changing the object Jellyfin hands out (as the startup
/// controller does) would leave Jellyfin's in-memory settings changed when the save throws.
/// Never touched: BaseUrl, LocalNetworkAddresses, RequireHttps, EnableHttps, CertificatePath, RemoteIPFilter.
/// </remarks>
internal sealed class JellyfinNetwork
{
    // One writer at a time for network.xml: the wizard, the health task's "Put it back" and Remove all come here.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly Dictionary<string, Setting> Settings = new Setting[]
    {
        new(NetworkKeys.KnownProxies, c => List(c.KnownProxies), (c, v) => c.KnownProxies = ToList(v)),
        new(NetworkKeys.PublishedServerUri, c => List(c.PublishedServerUriBySubnet), (c, v) => c.PublishedServerUriBySubnet = ToList(v)),
        new(NetworkKeys.EnableRemoteAccess, c => JsonSerializer.SerializeToElement(c.EnableRemoteAccess), (c, v) => c.EnableRemoteAccess = v.GetBoolean()),
        new(NetworkKeys.LocalNetworkSubnets, c => List(c.LocalNetworkSubnets), (c, v) => c.LocalNetworkSubnets = ToList(v)),
    }.ToDictionary(s => s.Key, StringComparer.Ordinal);

    private readonly IConfigurationManager _config;
    private readonly IXmlSerializer _xml;
    private readonly Journal _journal;
    private readonly IOptions<ForwardedHeadersOptions>? _live;
    private readonly ILogger _logger;

    internal JellyfinNetwork(IConfigurationManager config, IXmlSerializer xml, Journal journal, IOptions<ForwardedHeadersOptions>? live, ILogger logger)
    {
        _config = config;
        _xml = xml;
        _journal = journal;
        _live = live;
        _logger = logger;
    }

    /// <summary>
    /// Writes every setting in <see cref="NetworkKeys.All"/> order. Stops at the first one that fails, so nothing
    /// more is changed once something has gone wrong. Never throws except <see cref="OperationCanceledException"/>
    /// before it has started.
    /// </summary>
    internal async Task<IReadOnlyList<SettingResult>> ApplyAsync(NetworkPlan plan, CancellationToken ct)
    {
        var results = new List<SettingResult>();
        foreach (var key in NetworkKeys.All)
        {
            var result = await ApplyOneAsync(key, plan, ct).ConfigureAwait(false);
            results.Add(result);
            if (result.Outcome == SettingOutcome.Failed)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>Writes one setting. This is also "Put it back" (T20): the same journalled write, for one key.</summary>
    internal async Task<SettingResult> ApplyOneAsync(string key, NetworkPlan plan, CancellationToken ct)
    {
        if (!Settings.TryGetValue(key, out var setting))
        {
            return new SettingResult(key, SettingOutcome.Failed, SettingProblems.UnknownSetting);
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Apply(setting, plan);
        }
        catch (Exception e)
        {
            // Fail open: whatever went wrong, Jellyfin keeps running on what it has, and the type is all that's logged.
            _logger.LogWarning("Trailhead couldn't write Jellyfin's {Setting} ({Error})", key, e.GetType().Name);
            return new SettingResult(key, SettingOutcome.Failed, SettingProblems.SaveFailed);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Compares each journalled setting with what is saved now (T20). Known proxies that are saved but not yet in
    /// the live forwarded-headers options read as <see cref="DriftState.SavedNotInEffect"/>: Jellyfin builds those
    /// once at start (Jellyfin.Server/Startup.cs:73, ApiServiceCollectionExtensions.cs:116, v12.4).
    /// </summary>
    internal IReadOnlyList<DriftItem> CheckDrift()
    {
        var items = new List<DriftItem>();
        IReadOnlyList<JournalEntry> entries;
        NetworkConfiguration? saved;
        try
        {
            entries = _journal.Entries().Where(e => e.Kind == JournalKinds.JellyfinSetting).ToList();
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't read its undo journal ({Error})", e.GetType().Name);
            return items;
        }

        try
        {
            saved = ReadSaved();
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't read Jellyfin's network settings ({Error})", e.GetType().Name);
            saved = null;
        }

        foreach (var entry in entries)
        {
            if (saved is null || !Settings.TryGetValue(entry.Key, out var setting) || entry.Written is not { } written)
            {
                items.Add(new DriftItem(entry.Id, entry.Key, DriftState.Unknown, null));
                continue;
            }

            var now = setting.Get(saved);
            if (!JsonElement.DeepEquals(now, written))
            {
                items.Add(new DriftItem(entry.Id, entry.Key, DriftState.Changed, now));
                continue;
            }

            var state = entry.Key == NetworkKeys.KnownProxies ? LiveTrust(Added(entry)) : DriftState.InEffect;
            items.Add(new DriftItem(entry.Id, entry.Key, state, now));
        }

        return items;
    }

    /// <summary>
    /// Puts the "before" values back (T11) for the chosen entries, or every Jellyfin-setting entry when
    /// <paramref name="ids"/> is null. A value the admin has changed since is left alone unless
    /// <paramref name="force"/>. An entry is dropped only once its value is proven back.
    /// </summary>
    internal async Task<IReadOnlyList<RestoreResult>> RestoreAsync(IReadOnlyCollection<string>? ids, bool force, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var results = new List<RestoreResult>();
            IReadOnlyList<JournalEntry> entries;
            try
            {
                entries = _journal.Entries();
            }
            catch (Exception e)
            {
                _logger.LogWarning("Trailhead couldn't read its undo journal ({Error})", e.GetType().Name);
                return results;
            }

            var chosen = ids is null ? null : new HashSet<string>(ids, StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => chosen is null || chosen.Contains(e.Id)))
            {
                if (entry.Kind != JournalKinds.JellyfinSetting)
                {
                    if (chosen is not null)
                    {
                        results.Add(new RestoreResult(entry.Id, entry.Key, RestoreOutcome.NotMine));
                    }

                    continue;
                }

                RestoreResult result;
                try
                {
                    result = Restore(entry, force);
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Trailhead couldn't put back Jellyfin's {Setting} ({Error})", entry.Key, e.GetType().Name);
                    result = new RestoreResult(entry.Id, entry.Key, RestoreOutcome.Failed, SettingProblems.SaveFailed);
                }

                results.Add(result);
            }

            return results;
        }
        finally
        {
            Gate.Release();
        }
    }

    private RestoreResult Restore(JournalEntry entry, bool force)
    {
        if (!Settings.TryGetValue(entry.Key, out var setting) || entry.Before is not { } before)
        {
            return new RestoreResult(entry.Id, entry.Key, RestoreOutcome.NotMine, SettingProblems.UnknownSetting);
        }

        var copy = Copy(_config.GetNetworkConfiguration());
        var now = setting.Get(copy);
        if (JsonElement.DeepEquals(now, before))
        {
            _journal.Remove(new[] { entry.Id });
            return new RestoreResult(entry.Id, entry.Key, RestoreOutcome.AlreadyBefore);
        }

        if (!force && (entry.Written is not { } written || !JsonElement.DeepEquals(now, written)))
        {
            return new RestoreResult(entry.Id, entry.Key, RestoreOutcome.ChangedSince);
        }

        setting.Set(copy, before);
        var problem = SaveAndCheck(setting, copy, before);
        if (problem is not null)
        {
            return new RestoreResult(entry.Id, entry.Key, RestoreOutcome.Failed, problem);
        }

        _journal.Remove(new[] { entry.Id });
        return new RestoreResult(entry.Id, entry.Key, RestoreOutcome.Restored);
    }

    private SettingResult Apply(Setting setting, NetworkPlan plan)
    {
        var copy = Copy(_config.GetNetworkConfiguration());
        var entry = _journal.Find(JournalKinds.JellyfinSetting, setting.Key);
        var (target, outcome, problem) = Target(setting.Key, copy, plan, entry);
        if (target is not { } value)
        {
            return new SettingResult(setting.Key, outcome, problem);
        }

        var before = setting.Get(copy);
        if (JsonElement.DeepEquals(before, value))
        {
            return new SettingResult(setting.Key, SettingOutcome.AlreadySet);
        }

        try
        {
            _journal.Record(JournalKinds.JellyfinSetting, setting.Key, before, value);
        }
        catch (Exception e)
        {
            // No record, no change.
            _logger.LogWarning("Trailhead didn't change Jellyfin's {Setting}: its undo journal couldn't be written ({Error})", setting.Key, e.GetType().Name);
            return new SettingResult(setting.Key, SettingOutcome.Failed, SettingProblems.JournalUnavailable);
        }

        setting.Set(copy, value);
        problem = SaveAndCheck(setting, copy, value);
        return problem is null
            ? new SettingResult(setting.Key, SettingOutcome.Written)
            : new SettingResult(setting.Key, SettingOutcome.Failed, problem);
    }

    // Saves through Jellyfin and reads network.xml back. Null when the file holds the value; otherwise the problem.
    // The journal entry stays either way: if the save half-landed, its "before" is what puts things right.
    private string? SaveAndCheck(Setting setting, NetworkConfiguration copy, JsonElement expected)
    {
        try
        {
            _config.SaveConfiguration(NetworkConfigurationStore.StoreKey, copy);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Jellyfin didn't save its {Setting} ({Error})", setting.Key, e.GetType().Name);
            return SettingProblems.SaveFailed;
        }

        NetworkConfiguration saved;
        try
        {
            saved = ReadSaved();
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead couldn't read back Jellyfin's {Setting} ({Error})", setting.Key, e.GetType().Name);
            return SettingProblems.ReadBackDiffers;
        }

        if (!JsonElement.DeepEquals(setting.Get(saved), expected))
        {
            _logger.LogWarning("Jellyfin's saved {Setting} isn't what Trailhead wrote", setting.Key);
            return SettingProblems.ReadBackDiffers;
        }

        return null;
    }

    // What the setting should become, or no target with the reason. Each one leaves the admin's own entries alone.
    private static (JsonElement? Target, SettingOutcome Outcome, string? Problem) Target(string key, NetworkConfiguration current, NetworkPlan plan, JournalEntry? entry)
    {
        switch (key)
        {
            case NetworkKeys.KnownProxies:
            {
                var address = plan.ForwardingAddress.IsIPv4MappedToIPv6 ? plan.ForwardingAddress.MapToIPv4() : plan.ForwardingAddress;
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
                {
                    return (null, SettingOutcome.Failed, SettingProblems.InvalidPlan);
                }

                // An address Trailhead added for an earlier bind goes; the admin's own proxies stay.
                var ours = Ours(entry);
                var list = current.KnownProxies.Where(p => !ours.Contains(p) || SameAddress(p, address)).ToList();
                if (!list.Any(p => SameAddress(p, address)))
                {
                    list.Add(address.ToString());
                }

                return (List(list.ToArray()), SettingOutcome.Written, null);
            }

            case NetworkKeys.PublishedServerUri:
            {
                if (!TryUrl(plan, current.BaseUrl, out var url, out var problem))
                {
                    return (null, SettingOutcome.Failed, problem);
                }

                var wanted = "external=" + url;
                var ours = Ours(entry);
                var list = current.PublishedServerUriBySubnet.Where(p => !ours.Contains(p) || p == wanted).ToList();

                // Jellyfin lets an all= entry override everything and uses external= for every outside client
                // (NetworkManager.InitializeOverrides, NetworkManager.cs:499-540, v12.4): one of the admin's own
                // in the list means theirs and ours would fight. Theirs is kept, and reported.
                if (list.Any(p => p != wanted && IsOutsideEntry(p)))
                {
                    return (null, SettingOutcome.Conflict, SettingProblems.AdminPublishedUrl);
                }

                if (!list.Contains(wanted))
                {
                    list.Add(wanted);
                }

                return (List(list.ToArray()), SettingOutcome.Written, null);
            }

            case NetworkKeys.EnableRemoteAccess:
                return (JsonSerializer.SerializeToElement(true), SettingOutcome.Written, null);

            case NetworkKeys.LocalNetworkSubnets:
            {
                if (plan.LocalSubnet is not { } subnet)
                {
                    return (null, SettingOutcome.Skipped, SettingProblems.NotRequested);
                }

                if (!System.Net.IPNetwork.TryParse(subnet, out var network))
                {
                    return (null, SettingOutcome.Failed, SettingProblems.InvalidPlan);
                }

                var text = network.ToString();
                var ours = Ours(entry);
                var others = current.LocalNetworkSubnets.Where(s => !ours.Contains(s)).ToList();
                if (others.Contains(text))
                {
                    return (List(others.ToArray()), SettingOutcome.Written, null);
                }

                // A non-empty list replaces Jellyfin's whole private-range fallback (NetworkManager.cs:318-341), so
                // it is the admin's: Trailhead adds to an empty one only.
                if (others.Count > 0)
                {
                    return (null, SettingOutcome.Skipped, SettingProblems.ListNotEmpty);
                }

                return (List(new[] { text }), SettingOutcome.Written, null);
            }

            default:
                return (null, SettingOutcome.Failed, SettingProblems.UnknownSetting);
        }
    }

    /// <summary>The published URL: <c>https://name[:port]&lt;BaseUrl&gt;</c>, the port left out when it is 443.</summary>
    internal static bool TryUrl(NetworkPlan plan, string? baseUrl, out string url, out string? problem)
    {
        url = string.Empty;
        problem = null;
        var host = (plan.Hostname ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns || plan.ExternalPort is < 1 or > 65535)
        {
            problem = SettingProblems.InvalidPlan;
            return false;
        }

        // Jellyfin splits each entry on '=' and gives up on the rest of the list when it doesn't get two parts
        // (NetworkManager.cs:501-507, v12.4), so a base URL with '=' would break every entry after ours.
        var path = Campground.Shared.Network.JellyfinNetworkSettings.NormalizeBaseUrl(baseUrl);
        if (path.Contains('=', StringComparison.Ordinal) || path.Any(char.IsWhiteSpace))
        {
            problem = SettingProblems.BaseUrlUnusable;
            return false;
        }

        url = "https://" + host + (plan.ExternalPort == 443 ? string.Empty : ":" + plan.ExternalPort.ToString(System.Globalization.CultureInfo.InvariantCulture)) + path;
        return true;
    }

    private static bool IsOutsideEntry(string entry)
    {
        var eq = entry.IndexOf('=', StringComparison.Ordinal);
        var id = (eq < 0 ? entry : entry[..eq]).Trim();
        return id.Equals("external", StringComparison.OrdinalIgnoreCase) || id.Equals("all", StringComparison.OrdinalIgnoreCase);
    }

    // The list items Trailhead added: in what it wrote and not in what was there before.
    private static HashSet<string> Ours(JournalEntry? entry)
    {
        if (entry?.Written is not { ValueKind: JsonValueKind.Array } written)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var before = entry.Before is { ValueKind: JsonValueKind.Array } b ? ToList(b) : Array.Empty<string>();
        return ToList(written).Except(before, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<IPAddress> Added(JournalEntry entry)
        => Ours(entry).Select(p => IPAddress.TryParse(p, out var a) ? a : null).OfType<IPAddress>().ToList();

    private DriftState LiveTrust(IReadOnlyList<IPAddress> addresses)
    {
        try
        {
            var o = _live?.Value;
            if (o is null)
            {
                return DriftState.Unknown;
            }

            // Jellyfin turns forwarding off and clears both lists when Known proxies was empty at start
            // (ApiServiceCollectionExtensions.cs:171-176, v12.4).
            if (o.ForwardedHeaders == ForwardedHeaders.None)
            {
                return DriftState.SavedNotInEffect;
            }

            var trusted = addresses.All(a => o.KnownProxies.Any(p => Normal(p).Equals(Normal(a))) || o.KnownIPNetworks.Any(n => n.Contains(Normal(a))));
            return trusted ? DriftState.InEffect : DriftState.SavedNotInEffect;
        }
        catch (Exception)
        {
            return DriftState.Unknown;
        }
    }

    private static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    private static bool SameAddress(string entry, IPAddress address)
        => IPAddress.TryParse(entry, out var parsed) && Normal(parsed).Equals(address);

    private NetworkConfiguration ReadSaved()
    {
        // Where Jellyfin keeps it: <config dir>/<key>.xml (BaseConfigurationManager.GetConfigurationFile,
        // Emby.Server.Implementations/AppBase/BaseConfigurationManager.cs:271-274, v12.4).
        var path = Path.Combine(_config.CommonApplicationPaths.ConfigurationDirectoryPath, NetworkConfigurationStore.StoreKey + ".xml");
        return (NetworkConfiguration)(_xml.DeserializeFromFile(typeof(NetworkConfiguration), path)
            ?? throw new InvalidDataException("network.xml is empty."));
    }

    private NetworkConfiguration Copy(NetworkConfiguration from)
    {
        using var stream = new MemoryStream();
        _xml.SerializeToStream(from, stream);
        stream.Position = 0;
        return (NetworkConfiguration)(_xml.DeserializeFromStream(typeof(NetworkConfiguration), stream)
            ?? throw new InvalidDataException("The network settings didn't copy."));
    }

    private static JsonElement List(string[]? items) => JsonSerializer.SerializeToElement(items ?? Array.Empty<string>());

    private static string[] ToList(JsonElement value)
        => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray() : Array.Empty<string>();

    private sealed record Setting(string Key, Func<NetworkConfiguration, JsonElement> Get, Action<NetworkConfiguration, JsonElement> Set);
}

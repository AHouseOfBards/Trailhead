using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Trailhead.Settings;
using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

public sealed class DriftAndRestoreTests
{
    private static string Xml(NetworkConfiguration c)
    {
        using var s = new MemoryStream();
        new JellyfinXml().SerializeToStream(c, s);
        return Encoding.UTF8.GetString(s.ToArray());
    }

    private static DriftState StateOf(IReadOnlyList<DriftItem> items, string key) => items.Single(i => i.Key == key).State;

    [Fact]
    public async Task Known_proxies_read_as_saved_but_not_in_effect_until_Jellyfin_restarts()
    {
        // Started with no Known proxies: Jellyfin's live options have forwarding off (ApiServiceCollectionExtensions.cs:171-176).
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);

        var drift = rig.Network.CheckDrift();

        Assert.Equal(DriftState.SavedNotInEffect, StateOf(drift, NetworkKeys.KnownProxies));
        Assert.Equal(DriftState.InEffect, StateOf(drift, NetworkKeys.PublishedServerUri));
        Assert.Equal(DriftState.InEffect, StateOf(drift, NetworkKeys.EnableRemoteAccess));
    }

    [Fact]
    public async Task After_the_restart_the_live_options_trust_it_and_it_is_in_effect()
    {
        using var first = new NetworkRig(new NetworkConfiguration());
        await first.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);

        // The same files, with live options built from what is saved now, as the next start would.
        var restarted = new JellyfinNetwork(first.Config, new JellyfinXml(), first.Journal, Microsoft.Extensions.Options.Options.Create(NetworkRig.LiveOptionsAsJellyfinBuildsThem(first.Config.ReadFile())), first.Logger);

        Assert.Equal(DriftState.InEffect, StateOf(restarted.CheckDrift(), NetworkKeys.KnownProxies));
    }

    [Fact]
    public async Task A_live_list_that_still_has_the_old_proxies_only_is_not_in_effect()
    {
        // Jellyfin started with the admin's own proxy, so forwarding is on but our address isn't in the list yet.
        var start = new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } };
        var live = NetworkRig.LiveOptionsAsJellyfinBuildsThem(start);
        live.KnownIPNetworks.Clear();
        using var rig = new NetworkRig(start, live);
        await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan("192.168.1.20"), CancellationToken.None);

        Assert.Equal(DriftState.SavedNotInEffect, StateOf(rig.Network.CheckDrift(), NetworkKeys.KnownProxies));
    }

    [Fact]
    public async Task A_value_the_admin_changed_reads_as_changed_with_the_value_now()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);
        var edited = rig.Config.ReadFile();
        edited.EnableRemoteAccess = false;
        edited.KnownProxies = Array.Empty<string>();
        rig.Config.SaveConfiguration("network", edited);

        var drift = rig.Network.CheckDrift();

        var proxies = drift.Single(i => i.Key == NetworkKeys.KnownProxies);
        Assert.Equal(DriftState.Changed, proxies.State);
        Assert.Empty(proxies.Now!.Value.Deserialize<string[]>()!);
        Assert.Equal(DriftState.Changed, StateOf(drift, NetworkKeys.EnableRemoteAccess));
        Assert.Equal(DriftState.InEffect, StateOf(drift, NetworkKeys.PublishedServerUri));
    }

    [Fact]
    public async Task Drift_reads_the_saved_file_not_Jellyfins_memory()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        await rig.Network.ApplyOneAsync(NetworkKeys.EnableRemoteAccess, NetworkRig.Plan(), CancellationToken.None);

        // Someone edits network.xml by hand while Jellyfin runs.
        var onDisk = rig.Config.ReadFile();
        onDisk.EnableRemoteAccess = false;
        rig.Config.WriteFile(onDisk);

        Assert.Equal(DriftState.Changed, StateOf(rig.Network.CheckDrift(), NetworkKeys.EnableRemoteAccess));
    }

    [Fact]
    public async Task Put_it_back_reapplies_one_setting_and_keeps_the_original_before()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);
        var edited = rig.Config.ReadFile();
        edited.KnownProxies = new[] { "10.0.0.5", "10.0.0.6" };
        rig.Config.SaveConfiguration("network", edited);

        var result = await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(SettingOutcome.Written, result.Outcome);
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.6", "127.0.0.1" }, rig.Config.ReadFile().KnownProxies);
        Assert.Equal(new[] { "10.0.0.5" }, Assert.Single(rig.Journal.Entries()).Before!.Value.Deserialize<string[]>());
    }

    [Fact]
    public async Task Restore_puts_a_realistic_network_xml_back_exactly()
    {
        using var rig = new NetworkRig(JellyfinNetworkTests.Realistic());
        var original = File.ReadAllText(rig.Config.NetworkFile);
        await rig.Network.ApplyAsync(NetworkRig.Plan(subnet: "192.168.1.0/24"), CancellationToken.None);
        Assert.NotEqual(original, File.ReadAllText(rig.Config.NetworkFile));

        var results = await rig.Network.RestoreAsync(null, false, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(RestoreOutcome.Restored, r.Outcome));
        Assert.Equal(original, File.ReadAllText(rig.Config.NetworkFile));
        Assert.Equal(Xml(JellyfinNetworkTests.Realistic()), Xml(rig.Config.ReadFile()));
        Assert.Empty(rig.Journal.Entries());
    }

    [Fact]
    public async Task Restore_leaves_a_value_the_admin_changed_since_and_keeps_its_entry()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false, KnownProxies = new[] { "10.0.0.5" } });
        await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);
        var edited = rig.Config.ReadFile();
        edited.KnownProxies = new[] { "10.0.0.5", "127.0.0.1", "10.0.0.9" };
        rig.Config.SaveConfiguration("network", edited);

        var results = await rig.Network.RestoreAsync(null, false, CancellationToken.None);

        Assert.Equal(RestoreOutcome.ChangedSince, results.Single(r => r.Key == NetworkKeys.KnownProxies).Outcome);
        Assert.Equal(RestoreOutcome.Restored, results.Single(r => r.Key == NetworkKeys.EnableRemoteAccess).Outcome);
        Assert.Equal(new[] { "10.0.0.5", "127.0.0.1", "10.0.0.9" }, rig.Config.ReadFile().KnownProxies);
        Assert.False(rig.Config.ReadFile().EnableRemoteAccess);
        Assert.Equal(NetworkKeys.KnownProxies, Assert.Single(rig.Journal.Entries(), e => e.Kind == JournalKinds.JellyfinSetting).Key);
    }

    [Fact]
    public async Task Forced_restore_puts_the_before_back_even_over_the_admins_change()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);
        var edited = rig.Config.ReadFile();
        edited.KnownProxies = new[] { "10.0.0.9" };
        rig.Config.SaveConfiguration("network", edited);

        var result = Assert.Single(await rig.Network.RestoreAsync(null, true, CancellationToken.None));

        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.Equal(new[] { "10.0.0.5" }, rig.Config.ReadFile().KnownProxies);
    }

    [Fact]
    public async Task Restore_of_a_chosen_subset_touches_only_those()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);
        var remote = rig.Journal.Find(JournalKinds.JellyfinSetting, NetworkKeys.EnableRemoteAccess)!;

        var results = await rig.Network.RestoreAsync(new[] { remote.Id }, false, CancellationToken.None);

        Assert.Equal(NetworkKeys.EnableRemoteAccess, Assert.Single(results).Key);
        Assert.False(rig.Config.ReadFile().EnableRemoteAccess);
        Assert.Equal(new[] { "127.0.0.1" }, rig.Config.ReadFile().KnownProxies);
        Assert.Equal(2, rig.Journal.Entries().Count);
    }

    [Fact]
    public async Task Restore_whose_read_back_differs_keeps_the_entry()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        await rig.Network.ApplyOneAsync(NetworkKeys.EnableRemoteAccess, NetworkRig.Plan(), CancellationToken.None);
        rig.Config.OnDisk = c => c.EnableRemoteAccess = true;

        var result = Assert.Single(await rig.Network.RestoreAsync(null, false, CancellationToken.None));

        Assert.Equal(RestoreOutcome.Failed, result.Outcome);
        Assert.Single(rig.Journal.Entries());
    }

    [Fact]
    public async Task Restore_leaves_entries_of_other_kinds_alone()
    {
        using var rig = new NetworkRig(new NetworkConfiguration());
        var router = rig.Journal.Record(JournalKinds.RouterMapping, "router.443", null, JsonSerializer.SerializeToElement("x"));

        var all = await rig.Network.RestoreAsync(null, false, CancellationToken.None);
        var named = await rig.Network.RestoreAsync(new[] { router.Id }, false, CancellationToken.None);

        Assert.Empty(all);
        Assert.Equal(RestoreOutcome.NotMine, Assert.Single(named).Outcome);
        Assert.Single(rig.Journal.Entries());
    }

    [Fact]
    public void Drift_with_an_empty_journal_reports_nothing()
    {
        using var rig = new NetworkRig(new NetworkConfiguration(), new ForwardedHeadersOptions());

        Assert.Empty(rig.Network.CheckDrift());
    }
}

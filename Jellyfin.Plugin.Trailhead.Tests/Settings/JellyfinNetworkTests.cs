using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Trailhead.Settings;
using MediaBrowser.Common.Net;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

/// <summary>The writer against Jellyfin's real <see cref="NetworkConfiguration"/>, saved and read as network.xml.</summary>
public sealed class JellyfinNetworkTests
{
    private const string Url = "https://trailhead-test.example.com";

    /// <summary>A network.xml an admin with some history might have: a base URL, a bind address, their own proxy and a LAN override.</summary>
    internal static NetworkConfiguration Realistic() => new()
    {
        BaseUrl = "/jellyfin",
        EnableRemoteAccess = false,
        EnableIPv6 = true,
        LocalNetworkAddresses = new[] { "192.168.1.20", "127.0.0.1" },
        KnownProxies = new[] { "192.168.1.2" },
        PublishedServerUriBySubnet = new[] { "192.168.1.0/24=http://192.168.1.20:8096" },
        RemoteIPFilter = new[] { "203.0.113.0/24" },
        IsRemoteIPFilterBlacklist = true,
        RequireHttps = false,
        EnableHttps = true,
        CertificatePath = "/certs/own.pfx",
        InternalHttpPort = 8097,
    };

    private static string Xml(NetworkConfiguration c)
    {
        using var s = new MemoryStream();
        new JellyfinXml().SerializeToStream(c, s);
        return Encoding.UTF8.GetString(s.ToArray());
    }

    // Everything except the four settings Trailhead may write, as XML.
    private static string Untouchable(NetworkConfiguration c)
    {
        var copy = (NetworkConfiguration)new JellyfinXml().DeserializeFromBytes(typeof(NetworkConfiguration), Encoding.UTF8.GetBytes(Xml(c)))!;
        copy.KnownProxies = Array.Empty<string>();
        copy.PublishedServerUriBySubnet = Array.Empty<string>();
        copy.EnableRemoteAccess = false;
        copy.LocalNetworkSubnets = Array.Empty<string>();
        return Xml(copy);
    }

    [Fact]
    public async Task Setup_writes_the_four_settings_and_nothing_else()
    {
        using var rig = new NetworkRig(Realistic());
        var untouched = Untouchable(rig.Config.ReadFile());

        var results = await rig.Network.ApplyAsync(NetworkRig.Plan(subnet: "192.168.1.0/24"), CancellationToken.None);
        var saved = rig.Config.ReadFile();

        Assert.Equal(new[] { SettingOutcome.Written, SettingOutcome.Written, SettingOutcome.Written, SettingOutcome.Written }, results.Select(r => r.Outcome));
        Assert.Equal(new[] { "192.168.1.2", "127.0.0.1" }, saved.KnownProxies);
        Assert.Equal(new[] { "192.168.1.0/24=http://192.168.1.20:8096", "external=" + Url + "/jellyfin" }, saved.PublishedServerUriBySubnet);
        Assert.True(saved.EnableRemoteAccess);
        Assert.Equal(new[] { "192.168.1.0/24" }, saved.LocalNetworkSubnets);
        Assert.Equal(untouched, Untouchable(saved));
        Assert.Equal("/jellyfin", saved.BaseUrl);
        Assert.Equal(new[] { "192.168.1.20", "127.0.0.1" }, saved.LocalNetworkAddresses);
        Assert.Equal("/certs/own.pfx", saved.CertificatePath);
        Assert.Equal(4, rig.Journal.Entries().Count);
    }

    [Fact]
    public async Task Running_setup_again_writes_nothing_and_journals_nothing_new()
    {
        using var rig = new NetworkRig(Realistic());
        await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);
        var saves = rig.Config.Saves;
        var entries = rig.Journal.Entries().Select(e => (e.Id, e.At)).ToList();

        var again = await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);

        Assert.All(again.Take(3), r => Assert.Equal(SettingOutcome.AlreadySet, r.Outcome));
        Assert.Equal(saves, rig.Config.Saves);
        Assert.Equal(entries, rig.Journal.Entries().Select(e => (e.Id, e.At)).ToList());
        Assert.Single(rig.Config.ReadFile().KnownProxies, p => p == "127.0.0.1");
    }

    [Fact]
    public async Task A_setting_Jellyfin_already_has_is_not_journalled()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = true, KnownProxies = new[] { "127.0.0.1" } });

        var proxies = await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);
        var remote = await rig.Network.ApplyOneAsync(NetworkKeys.EnableRemoteAccess, NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(SettingOutcome.AlreadySet, proxies.Outcome);
        Assert.Equal(SettingOutcome.AlreadySet, remote.Outcome);
        Assert.Empty(rig.Journal.Entries());
        Assert.Equal(0, rig.Config.Saves);
    }

    [Fact]
    public async Task A_new_forwarding_address_replaces_only_the_one_Trailhead_added()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan("127.0.0.1"), CancellationToken.None);

        await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan("192.168.1.20"), CancellationToken.None);

        Assert.Equal(new[] { "10.0.0.5", "192.168.1.20" }, rig.Config.ReadFile().KnownProxies);
        Assert.Equal(new[] { "10.0.0.5" }, Assert.Single(rig.Journal.Entries()).Before!.Value.Deserialize<string[]>());
    }

    [Theory]
    [InlineData(443, "", Url)]
    [InlineData(8443, "", Url + ":8443")]
    [InlineData(10443, "/jf", Url + ":10443/jf")]
    [InlineData(443, "jf/", Url + "/jf")]
    public async Task The_published_url_carries_the_port_when_not_443_and_the_base_url(int port, string baseUrl, string expected)
    {
        using var rig = new NetworkRig(new NetworkConfiguration { BaseUrl = baseUrl });

        await rig.Network.ApplyOneAsync(NetworkKeys.PublishedServerUri, NetworkRig.Plan(port: port), CancellationToken.None);

        Assert.Equal(new[] { "external=" + expected }, rig.Config.ReadFile().PublishedServerUriBySubnet);
    }

    [Fact]
    public async Task Trailhead_replaces_its_own_published_url_when_the_port_moves()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { PublishedServerUriBySubnet = new[] { "192.168.1.0/24=http://192.168.1.20:8096" } });
        await rig.Network.ApplyOneAsync(NetworkKeys.PublishedServerUri, NetworkRig.Plan(), CancellationToken.None);

        var moved = await rig.Network.ApplyOneAsync(NetworkKeys.PublishedServerUri, NetworkRig.Plan(port: 8443), CancellationToken.None);

        Assert.Equal(SettingOutcome.Written, moved.Outcome);
        Assert.Equal(new[] { "192.168.1.0/24=http://192.168.1.20:8096", "external=" + Url + ":8443" }, rig.Config.ReadFile().PublishedServerUriBySubnet);
    }

    [Theory]
    [InlineData("external=https://media.example.org")]
    [InlineData("all=https://media.example.org")]
    [InlineData(" External =https://media.example.org")]
    public async Task The_admins_own_outside_url_is_kept_and_reported(string theirs)
    {
        using var rig = new NetworkRig(new NetworkConfiguration { PublishedServerUriBySubnet = new[] { theirs } });

        var result = await rig.Network.ApplyOneAsync(NetworkKeys.PublishedServerUri, NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(SettingOutcome.Conflict, result.Outcome);
        Assert.Equal(SettingProblems.AdminPublishedUrl, result.Problem);
        Assert.Equal(new[] { theirs }, rig.Config.ReadFile().PublishedServerUriBySubnet);
        Assert.Empty(rig.Journal.Entries());
    }

    [Fact]
    public async Task A_base_url_Jellyfin_would_split_wrongly_is_refused()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { BaseUrl = "/a=b" });

        var result = await rig.Network.ApplyOneAsync(NetworkKeys.PublishedServerUri, NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(SettingProblems.BaseUrlUnusable, result.Problem);
        Assert.Empty(rig.Config.ReadFile().PublishedServerUriBySubnet);
    }

    [Fact]
    public async Task The_lan_subnet_is_written_only_when_asked_and_only_into_an_empty_list()
    {
        using var empty = new NetworkRig(new NetworkConfiguration());
        using var theirs = new NetworkRig(new NetworkConfiguration { LocalNetworkSubnets = new[] { "10.10.0.0/16" } });

        var notAsked = await empty.Network.ApplyOneAsync(NetworkKeys.LocalNetworkSubnets, NetworkRig.Plan(), CancellationToken.None);
        var asked = await empty.Network.ApplyOneAsync(NetworkKeys.LocalNetworkSubnets, NetworkRig.Plan(subnet: "192.168.1.0/24"), CancellationToken.None);
        var full = await theirs.Network.ApplyOneAsync(NetworkKeys.LocalNetworkSubnets, NetworkRig.Plan(subnet: "192.168.1.0/24"), CancellationToken.None);

        Assert.Equal(SettingProblems.NotRequested, notAsked.Problem);
        Assert.Equal(SettingOutcome.Written, asked.Outcome);
        Assert.Equal(new[] { "192.168.1.0/24" }, empty.Config.ReadFile().LocalNetworkSubnets);
        Assert.Equal(SettingProblems.ListNotEmpty, full.Problem);
        Assert.Equal(new[] { "10.10.0.0/16" }, theirs.Config.ReadFile().LocalNetworkSubnets);
    }

    [Fact]
    public async Task A_read_back_that_differs_fails_and_the_journal_keeps_the_before()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        rig.Config.OnDisk = c => c.KnownProxies = new[] { "10.0.0.5" };

        var result = await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(SettingOutcome.Failed, result.Outcome);
        Assert.Equal(SettingProblems.ReadBackDiffers, result.Problem);
        Assert.Equal(new[] { "10.0.0.5" }, Assert.Single(rig.Journal.Entries()).Before!.Value.Deserialize<string[]>());
    }

    [Fact]
    public async Task A_failure_stops_setup_before_the_next_setting()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        rig.Config.FailNextSave = new IOException("disk full");

        var results = await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(new[] { NetworkKeys.KnownProxies }, results.Select(r => r.Key));
        Assert.False(rig.Config.ReadFile().EnableRemoteAccess);
    }

    [Fact]
    public async Task With_the_journal_unreadable_nothing_is_changed()
    {
        using var rig = new NetworkRig(new NetworkConfiguration());
        File.WriteAllText(Path.Combine(rig.Folder, "journal.json"), "garbage");
        var before = File.ReadAllText(rig.Config.NetworkFile);

        var results = await rig.Network.ApplyAsync(NetworkRig.Plan(), CancellationToken.None);

        Assert.Equal(SettingProblems.JournalUnavailable, Assert.Single(results).Problem);
        Assert.Equal(0, rig.Config.Saves);
        Assert.Equal(before, File.ReadAllText(rig.Config.NetworkFile));
    }

    [Fact]
    public async Task A_change_made_in_memory_by_a_failed_save_does_not_stick()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { EnableRemoteAccess = false });
        rig.Config.FailNextSave = new IOException("disk full");

        await rig.Network.ApplyOneAsync(NetworkKeys.EnableRemoteAccess, NetworkRig.Plan(), CancellationToken.None);

        // Jellyfin's own object was never touched: Trailhead changed and saved a copy.
        Assert.False(((NetworkConfiguration)rig.Config.GetConfiguration("network")).EnableRemoteAccess);
    }

    [Fact]
    public async Task Given_an_empty_plan_it_fails_and_writes_nothing()
    {
        using var rig = new NetworkRig(new NetworkConfiguration());
        var empty = new NetworkPlan(System.Net.IPAddress.Any, string.Empty, 0);

        var results = await rig.Network.ApplyAsync(empty, CancellationToken.None);

        Assert.Equal(SettingOutcome.Failed, Assert.Single(results).Outcome);
        Assert.Equal(0, rig.Config.Saves);
        Assert.Empty(rig.Journal.Entries());
    }
}

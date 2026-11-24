using System.Net;
using System.Net.NetworkInformation;
using Jellyfin.Plugin.Trailhead.Network;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Network;

public sealed class NatAndContainerTests
{
    [Theory]
    [InlineData("100.64.0.1", "203.0.113.7", "Cgnat")]
    [InlineData("100.127.255.254", null, "Cgnat")]
    [InlineData("192.168.0.2", "203.0.113.7", "DoubleNat")]
    [InlineData("10.0.0.2", "203.0.113.7", "DoubleNat")]
    [InlineData("172.16.5.1", "203.0.113.7", "DoubleNat")]
    [InlineData("172.31.255.1", null, "DoubleNat")]
    [InlineData("203.0.113.7", "203.0.113.8", "Cgnat")]
    [InlineData("203.0.113.7", "203.0.113.7", "Direct")]
    [InlineData("203.0.113.7", null, "Unknown")]
    [InlineData(null, "203.0.113.7", "Unknown")]
    [InlineData("0.0.0.0", "203.0.113.7", "Unknown")]
    [InlineData("169.254.1.1", "203.0.113.7", "Unknown")]
    [InlineData("100.128.0.1", "100.128.0.1", "Direct")]
    [InlineData("172.32.0.1", "172.32.0.1", "Direct")]
    public void T32_double_NAT_is_not_CGNAT(string? wan, string? observed, string expected)
        => Assert.Equal(expected, NatClassifier.Classify(wan is null ? null : IPAddress.Parse(wan), observed is null ? null : IPAddress.Parse(observed)).ToString());

    private sealed class Files : IFileProbe
    {
        private readonly Dictionary<string, string> _files;

        internal Files(params (string Path, string Text)[] files) => _files = files.ToDictionary(f => f.Path, f => f.Text);

        public bool Exists(string path) => _files.ContainsKey(path);

        public string? ReadText(string path) => _files.TryGetValue(path, out var t) ? t : null;
    }

    [Fact]
    public void Docker_is_found_by_its_marker_file_by_cgroup_v1_and_by_its_mounts_under_cgroup_v2()
    {
        Assert.True(ContainerCheck.InContainer(new Files(("/.dockerenv", string.Empty))));
        Assert.True(ContainerCheck.InContainer(new Files(("/run/.containerenv", string.Empty))));
        Assert.True(ContainerCheck.InContainer(new Files(("/proc/1/cgroup", "12:pids:/docker/0123abcd\n"))));
        Assert.True(ContainerCheck.InContainer(new Files(("/proc/1/cgroup", "0::/\n"), ("/proc/self/mountinfo", "1 2 0:1 /var/lib/docker/containers/abc/hostname /etc/hostname rw\n"))));
    }

    [Fact]
    public void A_plain_host_is_not_a_container()
    {
        Assert.False(ContainerCheck.InContainer(new Files()));
        Assert.False(ContainerCheck.InContainer(new Files(("/proc/1/cgroup", "0::/init.scope\n"), ("/proc/self/mountinfo", "22 1 8:1 / / rw\n"))));
        Assert.Equal(ContainerNetwork.None, ContainerCheck.Network(new Files(), Array.Empty<Nic>()));
    }

    private static Nic Nic(string name, string address, int prefix = 16)
        => new(name, name, name, NetworkInterfaceType.Ethernet, true, new[] { new NicAddress(IPAddress.Parse(address), prefix) }, Array.Empty<IPAddress>());

    [Fact]
    public void A_container_that_only_sees_its_own_eth0_on_a_docker_subnet_is_on_a_bridge()
    {
        var docker = new Files(("/.dockerenv", string.Empty));

        Assert.Equal(ContainerNetwork.Bridge, ContainerCheck.Network(docker, new[] { Nic("eth0", "172.17.0.5") }));
        Assert.Equal(ContainerNetwork.Bridge, ContainerCheck.Network(docker, new[] { Nic("eth0", "192.168.32.4", 20) }));
    }

    [Fact]
    public void A_container_that_sees_the_hosts_bridges_has_the_hosts_network()
    {
        var docker = new Files(("/.dockerenv", string.Empty));

        Assert.Equal(ContainerNetwork.Host, ContainerCheck.Network(docker, new[] { Nic("enp3s0", "192.168.1.50", 24), Nic("docker0", "172.17.0.1") }));
    }

    [Fact]
    public void A_container_on_the_LAN_with_no_bridge_in_sight_is_unknown_not_guessed()
    {
        var docker = new Files(("/.dockerenv", string.Empty));

        Assert.Equal(ContainerNetwork.Unknown, ContainerCheck.Network(docker, new[] { Nic("eth0", "192.168.1.60", 24) }));
    }

    [Fact]
    public void Linux_capabilities_are_read_from_CapEff()
    {
        var status = "Name:\tjellyfin\nCapInh:\t0000000000000000\nCapEff:\t0000000000001400\nCapBnd:\t000001ffffffffff\n";

        var caps = Elevation.EffectiveCaps(status);

        Assert.True(Elevation.Has(caps, 12)); // CAP_NET_ADMIN
        Assert.True(Elevation.Has(caps, 10)); // CAP_NET_BIND_SERVICE
        Assert.False(Elevation.Has(caps, 21));
        Assert.Equal(0UL, Elevation.EffectiveCaps(null));
        Assert.Equal(0UL, Elevation.EffectiveCaps(string.Empty));
    }

    [Fact]
    public void The_effective_uid_is_the_second_Uid_field_not_the_real_one()
    {
        // A setuid-style process: real uid 1000, effective 0.
        Assert.Equal(0, Elevation.EffectiveUid("Name:\tx\nUid:\t1000\t0\t0\t0\nGid:\t1000\t1000\t1000\t1000\n"));
        Assert.Equal(1000, Elevation.EffectiveUid("Uid:\t0\t1000\t1000\t1000\n"));
        Assert.Null(Elevation.EffectiveUid(null));
        Assert.Null(Elevation.EffectiveUid("Name:\tx\n"));
    }

    [Fact]
    public void The_privilege_check_runs_on_this_machine_without_throwing()
    {
        var p = Elevation.Current(Jellyfin.Plugin.Trailhead.Network.SystemFileProbe.Instance);

        // Whatever this machine is, an elevated token implies the firewall right.
        Assert.True(!p.Elevated || p.NetAdmin);
    }

    [Theory]
    [InlineData(0, "Public")]
    [InlineData(1, "Private")]
    [InlineData(2, "Domain")]
    [InlineData(7, "Unknown")]
    public void Windows_network_categories_map_to_profiles(int category, string profile)
        => Assert.Equal(profile, WindowsNetworkProfile.FromCategory(category).ToString());

    [Fact]
    public void An_adapter_id_that_is_not_a_guid_is_unknown_not_an_error()
    {
        Assert.Equal(NetworkProfile.Unknown, WindowsNetworkProfile.ForAdapter("eth0"));
        Assert.Equal(NetworkProfile.Unknown, WindowsNetworkProfile.ForAdapter(null));
    }

    [Fact]
    public void The_profile_of_a_real_adapter_is_read_without_throwing_and_an_unknown_adapter_is_unknown()
    {
        // Runs the real COM path on Windows (and the guard elsewhere). An adapter nobody has can only be Unknown.
        Assert.Equal(NetworkProfile.Unknown, WindowsNetworkProfile.ForAdapter(Guid.NewGuid().ToString("B")));
        foreach (var nic in SystemNetworkView.Instance.Interfaces())
        {
            _ = WindowsNetworkProfile.ForAdapter(nic.Id);
        }
    }
}

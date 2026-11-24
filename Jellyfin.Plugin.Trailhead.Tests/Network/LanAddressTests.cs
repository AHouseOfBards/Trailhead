using System.Net;
using System.Net.NetworkInformation;
using Jellyfin.Plugin.Trailhead.Network;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Network;

/// <summary>A4 / #26: the forward points at the address on the router's subnet, never a VPN, vEthernet or bridge.</summary>
public sealed class LanAddressTests
{
    private static readonly IPAddress Router = IPAddress.Parse("192.168.1.1");

    private static Nic Nic(string name, string description, string address, int prefix, string? gateway = null, NetworkInterfaceType type = NetworkInterfaceType.Ethernet, bool up = true)
        => new(Guid.NewGuid().ToString("B"), name, description, type, up, new[] { new NicAddress(IPAddress.Parse(address), prefix) }, gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) });

    [Fact]
    public void With_a_VPN_holding_the_default_route_the_LAN_address_is_still_chosen()
    {
        var nics = new[]
        {
            Nic("WireGuard Tunnel", "WireGuard Tunnel", "10.64.0.2", 32, "10.64.0.1"),
            Nic("Ethernet", "Intel(R) Ethernet", "192.168.1.50", 24, "192.168.1.1"),
        };

        Assert.Equal(IPAddress.Parse("192.168.1.50"), LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void WSL_and_Docker_switches_on_other_subnets_are_never_chosen()
    {
        var nics = new[]
        {
            Nic("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.24.80.1", 20),
            Nic("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", "172.17.0.1", 20),
            Nic("docker0", "docker0", "172.18.0.1", 16),
            Nic("Wi-Fi", "Intel(R) Wi-Fi 6", "192.168.1.73", 24, "192.168.1.1", NetworkInterfaceType.Wireless80211),
        };

        Assert.Equal(IPAddress.Parse("192.168.1.73"), LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void A_virtual_adapter_on_the_routers_subnet_loses_to_a_real_one()
    {
        var nics = new[]
        {
            Nic("tap0", "OpenVPN TAP-Windows6", "192.168.1.200", 24),
            Nic("Ethernet", "Realtek PCIe GbE", "192.168.1.50", 24, "192.168.1.1"),
        };

        Assert.Equal(IPAddress.Parse("192.168.1.50"), LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void Hyper_Vs_external_switch_is_used_when_it_is_the_only_way_to_the_router()
    {
        var nics = new[] { Nic("vEthernet (External)", "Hyper-V Virtual Ethernet Adapter", "192.168.1.50", 24, "192.168.1.1") };

        Assert.Equal(IPAddress.Parse("192.168.1.50"), LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void A_bridge_that_only_happens_to_share_the_subnet_and_has_no_route_there_is_not_used()
    {
        var nics = new[] { Nic("docker0", "docker0", "192.168.1.200", 24) };

        Assert.Null(LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void Behind_a_second_NAT_or_in_a_container_with_no_address_on_the_routers_subnet_there_is_no_guess()
    {
        var nics = new[] { Nic("eth0", "eth0", "172.17.0.5", 16, "172.17.0.1") };

        Assert.Null(LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void No_router_or_no_interfaces_is_no_address()
    {
        Assert.Null(LanAddressPicker.Pick(null, new[] { Nic("Ethernet", "x", "192.168.1.50", 24) }));
        Assert.Null(LanAddressPicker.Pick(Router, Array.Empty<Nic>()));
        Assert.Null(LanAddressPicker.Pick(IPAddress.Loopback, new[] { Nic("Ethernet", "x", "127.0.0.2", 8) }));
    }

    [Fact]
    public void A_down_interface_is_not_used()
    {
        var nics = new[] { Nic("Ethernet", "x", "192.168.1.50", 24, "192.168.1.1", up: false) };

        Assert.Null(LanAddressPicker.Pick(Router, nics));
    }

    [Fact]
    public void Gateways_list_real_interfaces_before_virtual_ones()
    {
        var nics = new[]
        {
            Nic("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.24.80.10", 20, "172.24.80.1"),
            Nic("Ethernet", "Realtek", "192.168.1.50", 24, "192.168.1.1"),
        };

        var gateways = LanAddressPicker.Gateways(nics);

        Assert.Equal(IPAddress.Parse("192.168.1.1"), gateways[0].Gateway);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), gateways[0].Local);
        Assert.Equal(2, gateways.Count);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.1", 24, true)]
    [InlineData("192.168.2.50", "192.168.1.1", 24, false)]
    [InlineData("10.1.200.3", "10.1.0.1", 16, true)]
    [InlineData("10.1.200.3", "10.1.0.1", 17, false)]
    public void Subnets_are_compared_bit_by_bit(string a, string b, int prefix, bool same)
        => Assert.Equal(same, LanAddressPicker.SameSubnet(IPAddress.Parse(a), IPAddress.Parse(b), prefix));
}

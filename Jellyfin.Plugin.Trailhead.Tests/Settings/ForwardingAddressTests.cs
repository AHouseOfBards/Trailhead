using System.Net;
using Jellyfin.Plugin.Trailhead.Settings;
using MediaBrowser.Model.Net;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

/// <summary>
/// The bind lists are the shapes <c>NetworkManager.GetAllBindInterfaces</c> returns at v12.4
/// (src/Jellyfin.Networking/Manager/NetworkManager.cs:807-850): one wildcard when no bind address is set (IPv6Any
/// with both families on, Any with IPv4 only), otherwise the filtered interfaces with their adapter names.
/// </summary>
public sealed class ForwardingAddressTests
{
    private static IPData Bind(string address, string name = "Ethernet")
    {
        var ip = IPAddress.Parse(address);
        return new IPData(ip, null, name);
    }

    public static TheoryData<string, IPData[], bool, bool, string?> Cases => new()
    {
        { "wildcard, both families on", new[] { new IPData(IPAddress.IPv6Any, null) }, true, true, "127.0.0.1" },
        { "wildcard, IPv4 only", new[] { new IPData(IPAddress.Any, null) }, true, false, "127.0.0.1" },
        { "IPv4 off: IPv6 interfaces, loopback among them", new[] { Bind("2001:db8::5"), Bind("::1", "Loopback Pseudo-Interface 1") }, false, true, "::1" },
        { "IPv4 off: IPv6 interfaces, no loopback", new[] { Bind("fe80::1"), Bind("2001:db8::5") }, false, true, "2001:db8::5" },
        { "explicit LAN only", new[] { Bind("192.168.1.20") }, true, false, "192.168.1.20" },
        { "explicit LAN, IPv6 listed first", new[] { Bind("2001:db8::5"), Bind("192.168.1.20") }, true, true, "192.168.1.20" },
        { "loopback listed after LAN", new[] { Bind("192.168.1.20"), Bind("127.0.0.1", "lo") }, true, false, "127.0.0.1" },
        { "IPv6 off with an IPv6 bind", new[] { Bind("2001:db8::5"), Bind("192.168.1.20") }, true, false, "192.168.1.20" },
        { "IPv6 off and only an IPv6 bind", new[] { Bind("2001:db8::5") }, true, false, null },
        { "IPv6 off, ::1 listed", new[] { Bind("::1", "lo"), Bind("192.168.1.20") }, true, false, "192.168.1.20" },
        { "vEthernet listed first", new[] { Bind("172.29.80.1", "vEthernet (WSL (Hyper-V firewall))"), Bind("192.168.1.20") }, true, false, "192.168.1.20" },
        { "Hyper-V default switch first", new[] { Bind("172.17.0.1", "vEthernet (Default Switch)"), Bind("10.0.0.7", "Wi-Fi") }, true, false, "10.0.0.7" },
        { "only a vEthernet", new[] { Bind("172.29.80.1", "vEthernet (WSL)") }, true, false, "172.29.80.1" },
        { "docker bridge first", new[] { Bind("172.17.0.1", "docker0"), Bind("192.168.1.20", "eth0") }, true, false, "192.168.1.20" },
        { "IPv4-mapped bind", new[] { new IPData(IPAddress.Parse("::ffff:192.168.1.20"), null, "eth0") }, true, false, "192.168.1.20" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Picks_the_address_Jellyfin_listens_on(string why, IPData[] binds, bool ipv4, bool ipv6, string? expected)
    {
        var chosen = ForwardingAddress.Choose(binds, ipv4, ipv6);

        Assert.True(expected == chosen?.ToString(), why + ": got " + (chosen?.ToString() ?? "none"));
    }

    [Fact]
    public void Never_the_name_localhost()
    {
        var chosen = ForwardingAddress.Choose(new[] { new IPData(IPAddress.IPv6Any, null) }, true, true);

        Assert.Equal("127.0.0.1", chosen!.ToString());
        Assert.DoesNotContain("localhost", chosen.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wildcard_with_IPv4_off_is_IPv6_loopback()
    {
        Assert.Equal(IPAddress.IPv6Loopback, ForwardingAddress.Choose(new[] { new IPData(IPAddress.IPv6Any, null) }, false, true));
    }

    [Fact]
    public void Given_no_binds_at_all_it_has_no_answer()
    {
        Assert.Null(ForwardingAddress.Choose(Array.Empty<IPData>(), true, true));
        Assert.Null(ForwardingAddress.Choose(null, true, true));
    }
}

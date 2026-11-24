using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Trailhead.Network;

/// <summary>One IPv4 or IPv6 address on an interface, with its prefix length.</summary>
internal sealed record NicAddress(IPAddress Address, int PrefixLength);

/// <summary>A network interface, copied out of the OS so tests can describe any machine.</summary>
internal sealed record Nic(string Id, string Name, string Description, NetworkInterfaceType Type, bool Up, IReadOnlyList<NicAddress> Addresses, IReadOnlyList<IPAddress> Gateways);

/// <summary>The machine's interfaces. A seam: tests hand in a VPN, a WSL switch, a Docker bridge.</summary>
internal interface INetworkView
{
    IReadOnlyList<Nic> Interfaces();
}

/// <summary>The interfaces as .NET sees them. Anything one interface won't say is left out rather than failing all.</summary>
internal sealed class SystemNetworkView : INetworkView
{
    internal static readonly SystemNetworkView Instance = new();

    public IReadOnlyList<Nic> Interfaces()
    {
        var list = new List<Nic>();
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return list;
        }

        foreach (var nic in all)
        {
            try
            {
                var props = nic.GetIPProperties();
                var addresses = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => new NicAddress(a.Address, a.PrefixLength))
                    .ToList();
                var gateways = props.GatewayAddresses.Select(g => g.Address).Where(g => !g.Equals(IPAddress.Any)).ToList();
                list.Add(new Nic(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus == OperationalStatus.Up, addresses, gateways));
            }
            catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
            {
                // One odd adapter (a half-removed VPN) must not hide the others.
            }
        }

        return list;
    }
}

/// <summary>
/// Which of this machine's addresses a router forward should point at (A4, #26): the one on the router's own subnet.
/// RASA first asked the routing table for "the address that reaches the internet", which is the VPN's address
/// whenever a VPN is up (a real report: setup done with a VPN on pointed the forward at the tunnel). Hyper-V's and
/// WSL's vEthernet adapters and Docker's bridges have the same problem on a smaller scale.
/// </summary>
internal static class LanAddressPicker
{
    // Matched against the interface name and description. Linux names are prefixes (docker0, br-1a2b, veth9f, wg0).
    private static readonly string[] VirtualWords =
    {
        "vethernet", "hyper-v", "default switch", "wsl", "docker", "virtualbox", "vmware", "vpn", "tap-", "tap-windows",
        "wireguard", "wintun", "tailscale", "zerotier", "openvpn", "nordlynx", "proton", "cisco anyconnect", "fortinet",
        "npcap", "loopback",
    };

    private static readonly string[] VirtualPrefixes = { "docker", "br-", "veth", "virbr", "vmnet", "vboxnet", "tun", "tap", "wg", "utun", "tailscale", "zt", "lxc", "lxd", "cni", "flannel", "cali", "podman", "ppp" };

    /// <summary>
    /// The IPv4 address of ours on the same subnet as <paramref name="router"/>, or null when there isn't one. Null is
    /// the answer behind a second NAT or from inside a Docker bridge: a guess would forward the router to an address
    /// nobody here holds, so none is made. A virtual adapter is used only if it is the one interface on the router's
    /// subnet and names that router as its gateway, which is Hyper-V's external switch (the host's real LAN address
    /// lives on "vEthernet (External)" there).
    /// </summary>
    internal static IPAddress? Pick(IPAddress? router, IReadOnlyList<Nic> nics)
    {
        if (router is null || router.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(router))
        {
            return null;
        }

        var candidates = new List<(Nic Nic, IPAddress Address)>();
        foreach (var nic in nics)
        {
            if (!nic.Up || nic.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var a in nic.Addresses)
            {
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.Equals(router) && a.PrefixLength is >= 8 and <= 30
                    && !IsLinkLocal(a.Address) && SameSubnet(a.Address, router, a.PrefixLength))
                {
                    candidates.Add((nic, a.Address));
                }
            }
        }

        var real = candidates.FirstOrDefault(c => !IsVirtual(c.Nic));
        if (real.Nic is not null)
        {
            return real.Address;
        }

        var virtualOnes = candidates.Where(c => c.Nic.Gateways.Contains(router)).ToList();
        return virtualOnes.Count == 1 && candidates.Count == 1 ? virtualOnes[0].Address : null;
    }

    /// <summary>The IPv4 gateways the OS knows of, router-facing interfaces first. Where SSDP is asked directly.</summary>
    internal static IReadOnlyList<(IPAddress Local, IPAddress Gateway)> Gateways(IReadOnlyList<Nic> nics)
    {
        var found = new List<(IPAddress, IPAddress, bool)>();
        foreach (var nic in nics.Where(n => n.Up && n.Type is not NetworkInterfaceType.Loopback))
        {
            foreach (var gw in nic.Gateways.Where(g => g.AddressFamily == AddressFamily.InterNetwork))
            {
                var local = nic.Addresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && SameSubnet(a.Address, gw, a.PrefixLength));
                if (local is not null)
                {
                    found.Add((local.Address, gw, IsVirtual(nic)));
                }
            }
        }

        return found.OrderBy(f => f.Item3).Select(f => (f.Item1, f.Item2)).Distinct().ToList();
    }

    internal static bool IsVirtual(Nic nic)
    {
        var name = nic.Name.ToLowerInvariant();
        var description = nic.Description.ToLowerInvariant();
        return nic.Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
            || VirtualWords.Any(w => name.Contains(w, StringComparison.Ordinal) || description.Contains(w, StringComparison.Ordinal))
            || VirtualPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
    }

    internal static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        if (x.Length != y.Length || prefix < 0 || prefix > x.Length * 8)
        {
            return false;
        }

        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = (byte)(0x80 >> (bit % 8));
            if ((x[bit / 8] & mask) != (y[bit / 8] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLinkLocal(IPAddress a) => a.GetAddressBytes() is [169, 254, ..];
}

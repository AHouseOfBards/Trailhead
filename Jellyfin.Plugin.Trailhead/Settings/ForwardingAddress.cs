using System.Net;
using System.Net.Sockets;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Net;

namespace Jellyfin.Plugin.Trailhead.Settings;

/// <summary>
/// The address Trailhead's listener forwards to Jellyfin from, which Jellyfin must trust as a proxy (03 §2b). It is
/// worked out from where Jellyfin listens, not assumed to be <c>127.0.0.1</c>: with "Bind to local network address"
/// set, Jellyfin listens only there and keeps loopback only when it's listed (<c>NetworkManager.FilterBindSettings</c>,
/// src/Jellyfin.Networking/Manager/NetworkManager.cs:371-395, v12.4).
/// </summary>
internal static class ForwardingAddress
{
    // Adapter names of virtual switches (Hyper-V, WSL, Docker, libvirt). Forwarding through one works only from that
    // host, and a WSL or Docker switch gets a new address at every boot (#26).
    private static readonly string[] VirtualNames = { "vEthernet", "Hyper-V", "WSL", "docker", "veth", "virbr", "br-" };

    /// <summary>
    /// Picks the address from Jellyfin's bind list (<c>INetworkManager.GetAllBindInterfaces()</c>) and its IPv4/IPv6
    /// switches. Null when no bound address can be used, which the caller reports rather than guess.
    /// </summary>
    /// <remarks>
    /// Never the name <c>localhost</c>: it can resolve to <c>::1</c>, and Jellyfin would trust the wrong family.
    /// IPv6 only when <c>EnableIPv6</c> is on, because otherwise Jellyfin drops an IPv6 Known proxy with no error
    /// (<c>AddIPAddress</c>, Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:310, v12.4).
    /// </remarks>
    internal static IPAddress? Choose(IReadOnlyList<IPData>? binds, bool ipv4Enabled, bool ipv6Enabled)
    {
        if (binds is null || binds.Count == 0)
        {
            return null;
        }

        // A wildcard is how Jellyfin says "no bind address set": IPv6Any when both families are on (Kestrel's dual
        // mode covers IPv4 too), IPv4 Any when only IPv4 is (GetAllBindInterfaces, NetworkManager.cs:807-850).
        if (binds.Any(b => b.Address.Equals(IPAddress.Any) || b.Address.Equals(IPAddress.IPv6Any)))
        {
            if (ipv4Enabled)
            {
                return IPAddress.Loopback;
            }

            return ipv6Enabled ? IPAddress.IPv6Loopback : null;
        }

        var usable = binds
            .Select(b => (Address: Normal(b.Address), Name: b.Name ?? string.Empty))
            .Where(b => Allowed(b.Address, ipv4Enabled, ipv6Enabled))
            .ToList();

        if (ipv4Enabled && usable.Any(b => b.Address.Equals(IPAddress.Loopback)))
        {
            return IPAddress.Loopback;
        }

        if (ipv6Enabled && usable.Any(b => b.Address.Equals(IPAddress.IPv6Loopback)))
        {
            return IPAddress.IPv6Loopback;
        }

        var lan = usable.Where(b => !IPAddress.IsLoopback(b.Address)).ToList();
        return First(lan.Where(b => !IsVirtual(b.Name))) ?? First(lan);
    }

    /// <summary>
    /// The address for this Jellyfin now, from its saved bind settings. Null when it can't be told. That list is
    /// what Kestrel bound at start, unless the admin has changed the binds since (the gate catches that, 03 §2b).
    /// </summary>
    internal static IPAddress? Current(INetworkManager network, IConfigurationManager config)
    {
        try
        {
            var n = config.GetNetworkConfiguration();
            return Choose(network.GetAllBindInterfaces(false), n.EnableIPv4, n.EnableIPv6);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool IsVirtual(string adapterName)
        => VirtualNames.Any(v => adapterName.Contains(v, StringComparison.OrdinalIgnoreCase));

    // IPv4 first, each family in Jellyfin's order.
    private static IPAddress? First(IEnumerable<(IPAddress Address, string Name)> candidates)
    {
        var list = candidates.ToList();
        return list.FirstOrDefault(b => b.Address.AddressFamily == AddressFamily.InterNetwork).Address
            ?? list.FirstOrDefault(b => b.Address.AddressFamily == AddressFamily.InterNetworkV6).Address;
    }

    private static IPAddress Normal(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool Allowed(IPAddress address, bool ipv4Enabled, bool ipv6Enabled)
    {
        if (address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => ipv4Enabled,

            // A link-local address is only good with its zone (%12), which a Known-proxies entry would have to carry
            // and which changes when the adapter does. Any other bound address is a steadier choice.
            AddressFamily.InterNetworkV6 => ipv6Enabled && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast,
            _ => false,
        };
    }
}

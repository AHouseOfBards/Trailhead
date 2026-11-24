using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Trailhead.Network;

internal enum NatKind
{
    /// <summary>No router answer (or no WAN address yet), so nothing can be said. The outside check decides.</summary>
    Unknown,

    /// <summary>The router's WAN address is the one the outside world sees: a forward on this router is enough.</summary>
    Direct,

    /// <summary>The router's WAN address is private: a second router in front, usually fixable (T32).</summary>
    DoubleNat,

    /// <summary>The router's WAN is in 100.64.0.0/10, or is public but not what the outside sees: the carrier's NAT (T5).</summary>
    Cgnat,
}

/// <summary>
/// T32. RASA's rule was "WAN ≠ observed means CGNAT", which sent fixable double-NAT setups (an ISP modem-router in
/// front) to the CGNAT dead end. Some carriers use private ranges for CGNAT too, so DoubleNat is a likely reason, not a
/// promise; the outside check has the last word.
/// </summary>
internal static class NatClassifier
{
    internal static NatKind Classify(IPAddress? routerWan, IPAddress? observedV4)
    {
        if (routerWan is null || routerWan.AddressFamily != AddressFamily.InterNetwork)
        {
            return NatKind.Unknown;
        }

        var b = routerWan.GetAddressBytes();
        if (b[0] == 100 && (b[1] & 0xC0) == 64)
        {
            return NatKind.Cgnat;
        }

        if (b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168))
        {
            return NatKind.DoubleNat;
        }

        // 0.0.0.0, link-local and the like: the router has no real WAN address to compare.
        if (!PublicAddressLookup.IsPublic(routerWan))
        {
            return NatKind.Unknown;
        }

        if (observedV4 is null)
        {
            return NatKind.Unknown;
        }

        return routerWan.Equals(observedV4) ? NatKind.Direct : NatKind.Cgnat;
    }
}

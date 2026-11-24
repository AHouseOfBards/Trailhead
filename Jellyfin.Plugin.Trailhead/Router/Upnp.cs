using System.Net;

namespace Jellyfin.Plugin.Trailhead.Router;

/// <summary>UPnP IGD error codes Trailhead acts on. Every other code is reported as it is.</summary>
internal static class UpnpCodes
{
    /// <summary>InvalidArgs. Some routers answer this to a permanent lease (RASA, live).</summary>
    internal const int InvalidArgs = 402;

    /// <summary>ActionFailed. Also seen for a permanent lease.</summary>
    internal const int ActionFailed = 501;

    /// <summary>NoSuchEntryInArray: no mapping on that port. Normal on a read, success on a delete.</summary>
    internal const int NoSuchEntry = 714;

    /// <summary>ConflictInMappingEntry: the port is mapped to another device. Never taken over.</summary>
    internal const int Conflict = 718;

    /// <summary>SamePortValuesRequired: the router can't map outside 443 to inside 8443 (A2).</summary>
    internal const int SamePortRequired = 724;

    /// <summary>OnlyPermanentLeasesSupported. Read the opposite way by some routers; worth one try with a lease.</summary>
    internal const int OnlyPermanentLeases = 725;

    /// <summary>Codes after which the same mapping is asked for again with a finite lease.</summary>
    internal static bool AsksForALease(int code) => code is InvalidArgs or ActionFailed or OnlyPermanentLeases;
}

/// <summary>A port mapping as the router reports it (GetSpecificPortMappingEntry), never as it was asked for.</summary>
/// <param name="LeaseSeconds">0 is permanent. Otherwise what the router says is left (miniupnpd counts down).</param>
internal sealed record PortMapping(int ExternalPort, string Protocol, int InternalPort, string InternalClient, string Description, int LeaseSeconds, bool Enabled)
{
    internal bool Permanent => LeaseSeconds == 0;

    /// <summary>
    /// Whether this mapping sends traffic to <paramref name="client"/>. Routers write the address back however they
    /// like ("192.168.001.050" has been seen), so it is parsed rather than compared as text; one that doesn't parse
    /// counts as someone else's, which leaves the mapping alone rather than taking it.
    /// </summary>
    internal bool PointsAt(string? client)
        => !string.IsNullOrWhiteSpace(client) && Normalise(InternalClient) is { } mine && Normalise(client) is { } theirs && mine.Equals(theirs);

    private static IPAddress? Normalise(string text)
    {
        var trimmed = text.Trim();

        // Dotted IPv4 is read as decimal by hand first: IPAddress.TryParse reads a leading zero as octal, so
        // "192.168.001.050" would come out as .40 and a mapping to us would look like someone else's.
        var parts = trimmed.Split('.');
        if (parts.Length != 4)
        {
            return IPAddress.TryParse(trimmed, out var parsed) && trimmed.Contains(':', StringComparison.Ordinal)
                ? (parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed)
                : null;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return new IPAddress(bytes);
    }
}

/// <summary>A SOAP fault from the router. The router's own description is kept apart from the message: it is router text.</summary>
internal sealed class UpnpException : Exception
{
    internal UpnpException(string action, int code, string? description)
        : base("The router refused " + action + " with UPnP error " + code + ".")
    {
        Action = action;
        Code = code;
        RouterText = description ?? string.Empty;
    }

    internal string Action { get; }

    internal int Code { get; }

    internal string RouterText { get; }
}

/// <summary>A router's port-mapping service: where to send SOAP, and which service type to name.</summary>
internal sealed record IgdService(Uri ControlUrl, string ServiceType);

/// <summary>A router that answered SSDP and described a WAN connection service.</summary>
/// <param name="Address">The address the SSDP answer came from: the router's address on this network.</param>
internal sealed record IgdDevice(IgdService Service, IPAddress Address, string Manufacturer, string Model, Uri? AdminPage);

/// <summary>What protocols a forward can be for. Text on the wire is "TCP" / "UDP".</summary>
internal static class Protocols
{
    internal const string Tcp = "TCP";
    internal const string Udp = "UDP";

    internal static string Normalise(string? protocol)
        => string.Equals(protocol, Udp, StringComparison.OrdinalIgnoreCase) ? Udp
         : string.IsNullOrEmpty(protocol) || string.Equals(protocol, Tcp, StringComparison.OrdinalIgnoreCase) ? Tcp
         : throw new ArgumentException("Protocol must be TCP or UDP.", nameof(protocol));
}

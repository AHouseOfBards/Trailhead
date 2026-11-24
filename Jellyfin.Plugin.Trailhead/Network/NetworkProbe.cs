using System.Net;
using Jellyfin.Plugin.Trailhead.Router;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Network;

/// <summary>Setup step 1's facts (§3 step 1). Every part can be unknown; none of them stops the others.</summary>
/// <param name="LanAddress">This machine's address on the router's subnet (A4), or null when there is none.</param>
/// <param name="RouterWan">The router's own WAN address (GetExternalIPAddress), or null.</param>
internal sealed record ProbeReport(
    RouterStatus Router,
    string RouterMaker,
    string RouterModel,
    Uri? RouterAdminPage,
    IPAddress? LanAddress,
    IPAddress? RouterWan,
    PublicAddresses Public,
    NatKind Nat,
    ContainerNetwork Container,
    NetworkProfile WindowsProfile,
    Privilege Privilege,
    IReadOnlyList<PortState> Ports);

/// <summary>Runs the probe. Read-only: it asks the router questions and never changes anything on it.</summary>
internal sealed class NetworkProbe
{
    private readonly IRouterFinder _finder;
    private readonly IPublicAddressLookup _public;
    private readonly INetworkView _view;
    private readonly IFileProbe _files;
    private readonly Func<IgdService, IgdClient> _client;
    private readonly ILogger _logger;

    internal NetworkProbe(IRouterFinder finder, IPublicAddressLookup publicLookup, INetworkView view, IFileProbe files, Func<IgdService, IgdClient> client, ILogger logger)
    {
        _finder = finder;
        _public = publicLookup;
        _view = view;
        _files = files;
        _client = client;
        _logger = logger;
    }

    internal async Task<ProbeReport> RunAsync(IReadOnlyList<int> ports, CancellationToken ct)
    {
        var routerTask = Safe(() => _finder.FindAsync(ct), new RouterSearchResult(RouterStatus.NoReply, null), "router search");
        var publicTask = Safe(() => _public.LookupAsync(ct), new PublicAddresses(null, null), "public address");

        var nics = SafeSync(() => _view.Interfaces(), Array.Empty<Nic>(), "interfaces");
        var container = SafeSync(() => ContainerCheck.Network(_files, nics), ContainerNetwork.Unknown, "container");
        var privilege = SafeSync(() => Elevation.Current(_files), new Privilege(false, false, false), "privilege");
        var portStates = ports.Select(p => SafeSync(() => PortHolder.Check(p), new PortState(p, false, null, null), "port")).ToList();

        var router = await routerTask.ConfigureAwait(false);
        IPAddress? lan = null;
        IPAddress? wan = null;
        if (router.Device is { } device)
        {
            lan = LanAddressPicker.Pick(device.Address, nics);
            wan = await Safe<IPAddress?>(() => _client(device.Service).GetExternalAddressAsync(ct), null, "router WAN address").ConfigureAwait(false);
        }

        var profile = NetworkProfile.Unknown;
        if (lan is not null && nics.FirstOrDefault(n => n.Addresses.Any(a => a.Address.Equals(lan))) is { } nic)
        {
            profile = SafeSync(() => WindowsNetworkProfile.ForAdapter(nic.Id), NetworkProfile.Unknown, "network profile");
        }

        var observed = await publicTask.ConfigureAwait(false);
        var nat = NatClassifier.Classify(wan, observed.V4);

        // One plain line per decision (A6). Kinds only: addresses stay out of the log.
        _logger.LogInformation(
            "Trailhead checked the network: router {Router}, address on the router's network {Lan}, outside IPv4 {V4}, IPv6 {V6}, {Nat}, container {Container}, Windows profile {Profile}.",
            router.Status,
            lan is null ? "not found" : "found",
            observed.V4 is null ? "not found" : "found",
            observed.V6 is null ? "not found" : "found",
            nat,
            container,
            profile);

        return new ProbeReport(router.Status, router.Manufacturer, router.Model, router.Device?.AdminPage, lan, wan, observed, nat, container, profile, privilege, portStates);
    }

    private async Task<T> Safe<T>(Func<Task<T>> work, T fallback, string what)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // The type only: router text and URLs can carry anything.
            _logger.LogDebug("Trailhead's {What} check failed ({Error}).", what, e.GetType().Name);
            return fallback;
        }
    }

    private T SafeSync<T>(Func<T> work, T fallback, string what)
    {
        try
        {
            return work();
        }
        catch (Exception e)
        {
            _logger.LogDebug("Trailhead's {What} check failed ({Error}).", what, e.GetType().Name);
            return fallback;
        }
    }
}

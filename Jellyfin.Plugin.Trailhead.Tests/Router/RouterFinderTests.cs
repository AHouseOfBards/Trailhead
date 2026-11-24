using System.Net;
using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Router;

/// <summary>SSDP and the device description, over real UDP and HTTP to the fake IGD.</summary>
public sealed class RouterFinderTests
{
    [Fact]
    public async Task Finds_the_router_its_service_and_who_made_it()
    {
        await using var rig = await RouterRig.StartAsync();

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.Found, found.Status);
        Assert.Equal(("FakeRouter Inc.", "FR-1000"), (found.Manufacturer, found.Model));
        Assert.Equal(FakeIgd.V1, found.Device!.Service.ServiceType);
        Assert.Equal(new Uri(rig.Igd.Http, "igd1/ctl"), found.Device.Service.ControlUrl);
        Assert.Equal(new Uri(rig.Igd.Http, "admin"), found.Device.AdminPage);
        Assert.Equal(IPAddress.Loopback, found.Device.Address);
    }

    [Fact]
    public async Task A_v2_only_router_that_ignores_v1_searches_is_still_found()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.ServiceType = FakeIgd.V2;
        rig.Igd.Mode = SsdpMode.V2Only;

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.Found, found.Status);
        Assert.Equal(FakeIgd.V2, found.Device!.Service.ServiceType);
    }

    [Fact]
    public async Task The_search_asks_for_both_IGD_versions_the_WAN_services_and_root_devices()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Mode = SsdpMode.Silent;

        await rig.Finder.FindAsync(CancellationToken.None);

        var asked = rig.Igd.Searches.ToHashSet();
        Assert.Contains(FakeIgd.V1, asked);
        Assert.Contains(FakeIgd.V2, asked);
        Assert.Contains("urn:schemas-upnp-org:service:WANPPPConnection:1", asked);
        Assert.Contains("upnp:rootdevice", asked);

        // Twice each: one lost datagram on busy Wi-Fi must not read as "UPnP is off".
        Assert.Equal(2, rig.Igd.Searches.Count(s => s == FakeIgd.V1));
    }

    [Fact]
    public async Task Silence_is_no_reply_not_a_router()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Mode = SsdpMode.Silent;

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.NoReply, found.Status);
        Assert.Null(found.Device);
    }

    [Fact]
    public async Task An_empty_description_is_not_a_router()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.DescriptionOverride = string.Empty;

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.NoDescription, found.Status);
        Assert.Null(found.Device);
    }

    [Fact]
    public async Task A_gateway_with_no_port_mapping_service_says_so_and_names_itself()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.DescriptionOverride = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device>"
            + "<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><manufacturer>MediaOnly</manufacturer><modelName>M1</modelName>"
            + "<serviceList><service><serviceType>urn:schemas-upnp-org:service:ContentDirectory:1</serviceType><controlURL>/igd1/cd</controlURL></service></serviceList>"
            + "</device></root>";

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.NoPortService, found.Status);
        Assert.Equal("MediaOnly", found.Manufacturer);
        Assert.Null(found.Device);
    }

    [Fact]
    public async Task An_answer_without_a_location_is_ignored()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.Mode = SsdpMode.NoLocation;

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.NoReply, found.Status);
    }

    [Fact]
    public async Task A_description_on_another_host_than_the_one_that_answered_is_not_used()
    {
        // SSDP answers from 127.0.0.2; the description it points at is real and served on 127.0.0.1.
        await using var rig = await RouterRig.StartAsync(IPAddress.Parse("127.0.0.2"));

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.Equal(RouterStatus.NoReply, found.Status);
        Assert.Empty(rig.Igd.Calls);
    }

    [Fact]
    public async Task A_control_url_on_another_host_is_not_used()
    {
        await using var rig = await RouterRig.StartAsync();
        rig.Igd.DescriptionOverride = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device>"
            + "<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType><manufacturer>Odd</manufacturer>"
            + "<serviceList><service><serviceType>" + FakeIgd.V1 + "</serviceType><controlURL>http://192.0.2.10/ctl</controlURL></service></serviceList>"
            + "</device></root>";

        var found = await rig.Finder.FindAsync(CancellationToken.None);

        Assert.NotEqual(RouterStatus.Found, found.Status);
    }

    [Fact]
    public void A_header_is_read_whatever_its_case_and_missing_is_null()
    {
        var reply = "HTTP/1.1 200 OK\r\nlocation: http://192.168.1.1:5000/desc.xml\r\nST: upnp:rootdevice\r\n\r\n";

        Assert.Equal("http://192.168.1.1:5000/desc.xml", RouterFinder.Header(reply, "LOCATION"));
        Assert.Null(RouterFinder.Header(reply, "USN"));
        Assert.Null(RouterFinder.Header(string.Empty, "LOCATION"));
    }
}

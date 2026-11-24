using Jellyfin.Plugin.Trailhead.Router;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Router;

/// <summary>The SOAP client against the fake IGD, and the parsing facts RASA learned from real routers.</summary>
public sealed class IgdClientTests
{
    private static async Task<(FakeIgd Igd, IgdClient Client, HttpClient Http)> StartAsync()
    {
        var igd = await FakeIgd.StartAsync();
        var http = new HttpClient();
        return (igd, new IgdClient(http, new IgdService(new Uri(igd.Http, "igd1/ctl"), igd.ServiceType)), http);
    }

    [Fact]
    public async Task Reads_the_routers_WAN_address_and_treats_0000_as_none()
    {
        var (igd, client, http) = await StartAsync();
        await using (igd)
        using (http)
        {
            Assert.Equal("203.0.113.7", (await client.GetExternalAddressAsync(CancellationToken.None))!.ToString());

            igd.WanAddress = "0.0.0.0";
            Assert.Null(await client.GetExternalAddressAsync(CancellationToken.None));

            igd.WanAddress = "not an address";
            Assert.Null(await client.GetExternalAddressAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_missing_mapping_is_null_and_deleting_it_succeeds()
    {
        var (igd, client, http) = await StartAsync();
        await using (igd)
        using (http)
        {
            Assert.Null(await client.GetAsync(443, "TCP", CancellationToken.None));
            await client.DeleteAsync(443, "TCP", CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_conflict_is_an_UpnpException_carrying_718()
    {
        var (igd, client, http) = await StartAsync();
        await using (igd)
        using (http)
        {
            igd.Seed(443, 443, "192.168.1.77");

            var e = await Assert.ThrowsAsync<UpnpException>(() => client.AddAsync(443, "TCP", 8443, "192.168.1.50", 0, CancellationToken.None));

            Assert.Equal(UpnpCodes.Conflict, e.Code);

            // The router's own text stays out of the message (it is router text, and goes nowhere near a log).
            Assert.DoesNotContain("Fake error", e.Message);
        }
    }

    [Fact]
    public void An_answer_with_no_mapping_in_it_is_not_parsed_as_an_empty_one()
    {
        // RASA: a decode that found nothing yielded a zero mapping, read as "exists, permanent, disabled".
        Assert.Null(IgdClient.Parse(Array.Empty<byte>()));
        Assert.Null(IgdClient.Parse("<not xml"u8.ToArray()));
    }

    [Fact]
    public void A_DTD_in_a_routers_answer_is_refused()
    {
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaa\">]><x>&a;</x>"u8.ToArray();

        Assert.Null(IgdClient.Parse(evil));
    }

    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("192.168.001.050", true)]
    [InlineData(" 192.168.1.50 ", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("192.168.1.500", false)]
    [InlineData("", false)]
    [InlineData("router", false)]
    public void Client_addresses_are_compared_as_addresses_not_text(string reported, bool same)
    {
        var mapping = new PortMapping(443, "TCP", 8443, reported, IgdClient.Description, 0, true);

        Assert.Equal(same, mapping.PointsAt("192.168.1.50"));
    }

    [Fact]
    public void The_description_names_Trailhead_and_Jellyfin()
    {
        Assert.Equal("Trailhead (Jellyfin)", IgdClient.Description);
    }
}

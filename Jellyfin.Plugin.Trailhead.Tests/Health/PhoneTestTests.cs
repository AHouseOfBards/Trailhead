using System.Net;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Tests.Edge;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

/// <summary>
/// The phone test through a real Edge (Kestrel, TLS, the real pipeline). Clients come from different loopback source
/// addresses; <c>127.0.0.9</c> stands for a phone on the home Wi-Fi and the rest for a phone outside.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class PhoneTestTests : IAsyncLifetime
{
    private static readonly IPAddress HomePhone = IPAddress.Parse("127.0.0.9");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private EdgeRig _rig = null!;
    private PhoneTest _phone = null!;

    public async Task InitializeAsync()
    {
        _rig = await EdgeRig.StartAsync();
        _phone = new PhoneTest(_time);
        _rig.Phone = _phone;
        _rig.Watch = new PublicAddressWatch(_time);
        _rig.IsHome = ip => ip.Equals(HomePhone);
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private async Task<(HttpStatusCode Status, string Body)> OpenAsync(string path, string from = "127.0.0.5")
    {
        using var client = _rig.Client(from);
        using var response = await client.Http.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private bool JellyfinSaw(string fragment) => _rig.Jellyfin.All.Any(s => s.PathAndQuery.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task With_no_link_made_every_phone_path_is_a_404_and_never_reaches_Jellyfin()
    {
        await _rig.StartEdgeAsync();
        Assert.True(_rig.Service!.Gate.IsOpen);

        var (status, _) = await OpenAsync("/.trailhead/phone/" + new string('a', 32));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(PhoneTestStatus.None, _phone.State.Status);
        Assert.False(JellyfinSaw(".trailhead"));
    }

    [Fact]
    public async Task A_link_opened_from_outside_works_once()
    {
        await _rig.StartEdgeAsync();
        var token = _phone.Create();

        var first = await OpenAsync("/.trailhead/phone/" + token.Token);
        var second = await OpenAsync("/.trailhead/phone/" + token.Token);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Contains("It works", first.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, second.Status);
        var state = _phone.State;
        Assert.Equal(PhoneTestStatus.Passed, state.Status);
        Assert.Equal("IPv4", state.Family);
        Assert.False(JellyfinSaw(".trailhead"));
    }

    [Fact]
    public async Task A_link_expires_after_fifteen_minutes()
    {
        await _rig.StartEdgeAsync();
        var token = _phone.Create();
        _time.Advance(TimeSpan.FromMinutes(15));

        var (status, _) = await OpenAsync("/.trailhead/phone/" + token.Token);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(PhoneTestStatus.Expired, _phone.State.Status);
    }

    [Fact]
    public async Task A_link_just_inside_fifteen_minutes_still_works()
    {
        await _rig.StartEdgeAsync();
        var token = _phone.Create();
        _time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));

        var (status, _) = await OpenAsync("/.trailhead/phone/" + token.Token);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Theory]
    [InlineData("/.trailhead/phone/0123456789abcdef0123456789abcdef")]
    [InlineData("/.trailhead/phone/")]
    [InlineData("/.trailhead/")]
    [InlineData("/.trailhead/other")]
    [InlineData("/.TRAILHEAD/phone/x")]
    public async Task A_wrong_token_or_any_other_path_there_is_a_404_and_never_reaches_Jellyfin(string path)
    {
        await _rig.StartEdgeAsync();
        var token = _phone.Create();

        var (status, _) = await OpenAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.False(JellyfinSaw(".trailhead"));
        Assert.Equal(PhoneTestStatus.Waiting, _phone.State.Status);

        // The real token is still live: a wrong guess didn't use it up.
        Assert.Equal(HttpStatusCode.OK, (await OpenAsync("/.trailhead/phone/" + token.Token)).Status);
    }

    [Fact]
    public async Task A_new_link_retires_the_one_before()
    {
        await _rig.StartEdgeAsync();
        var old = _phone.Create();
        var current = _phone.Create();

        Assert.Equal(HttpStatusCode.NotFound, (await OpenAsync("/.trailhead/phone/" + old.Token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await OpenAsync("/.trailhead/phone/" + current.Token)).Status);
    }

    [Fact]
    public async Task A_hit_from_home_is_not_proof_and_leaves_the_link_live_for_the_phone_on_mobile_data()
    {
        await _rig.StartEdgeAsync();
        var token = _phone.Create();

        var home = await OpenAsync("/.trailhead/phone/" + token.Token, from: HomePhone.ToString());

        Assert.Equal(HttpStatusCode.OK, home.Status);
        Assert.DoesNotContain("It works", home.Body, StringComparison.Ordinal);
        Assert.Contains("Wi-Fi", home.Body, StringComparison.Ordinal);
        Assert.Equal(PhoneTestStatus.FromHome, _phone.State.Status);

        var outside = await OpenAsync("/.trailhead/phone/" + token.Token);
        Assert.Equal(HttpStatusCode.OK, outside.Status);
        Assert.Equal(PhoneTestStatus.Passed, _phone.State.Status);
    }

    [Fact]
    public async Task A_hit_from_our_own_public_address_is_home_and_is_counted_as_hairpin()
    {
        _rig.IsHome = _ => false;
        _rig.Watch!.SetOwn(IPAddress.Parse("127.0.0.7"));
        await _rig.StartEdgeAsync();
        var token = _phone.Create();

        var (status, body) = await OpenAsync("/.trailhead/phone/" + token.Token, from: "127.0.0.7");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("It works", body, StringComparison.Ordinal);
        Assert.Equal(PhoneTestStatus.FromHome, _phone.State.Status);
        Assert.True(_rig.Watch.Count >= 1);
        Assert.NotNull(_rig.Watch.LastSeen);
    }

    [Fact]
    public async Task With_the_gate_shut_the_phone_path_gets_the_503_and_the_link_stays_unused()
    {
        // Jellyfin's live options trust nobody, so the gate stays shut (not_trusted).
        _rig.Live = () => new ForwardedHeadersOptions();
        await _rig.StartEdgeAsync();
        Assert.False(_rig.Service!.Gate.IsOpen);
        var token = _phone.Create();

        var (status, _) = await OpenAsync("/.trailhead/phone/" + token.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(PhoneTestStatus.Waiting, _phone.State.Status);
    }

    [Fact]
    public async Task Edge_with_no_home_rule_given_treats_every_hit_as_home()
    {
        _rig.IsHome = null;
        await _rig.StartEdgeAsync();
        var token = _phone.Create();

        await OpenAsync("/.trailhead/phone/" + token.Token);

        Assert.Equal(PhoneTestStatus.FromHome, _phone.State.Status);
    }

    [Theory]
    [InlineData("10.1.2.3", false, true)]
    [InlineData("192.168.1.20", false, true)]
    [InlineData("100.64.0.9", false, true)]
    [InlineData("fd00::1", false, true)]
    [InlineData("::ffff:192.168.1.20", false, true)]
    [InlineData("203.0.113.5", false, false)]
    [InlineData("203.0.113.5", true, true)]
    [InlineData("2001:db8::5", false, false)]
    public void The_home_rule_is_Jellyfins_LAN_plus_every_address_that_is_not_public(string address, bool jellyfinLocal, bool home)
    {
        var rule = EdgeService.HomeRule(_ => jellyfinLocal);

        Assert.Equal(home, rule(IPAddress.Parse(address)));
    }
}

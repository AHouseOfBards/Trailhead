using System.Net;
using Campground.Shared.Registry;
using Jellyfin.Plugin.Trailhead.Edge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>The gate (T29): Edge forwards nothing for anyone but its own self-test until trust and the self-test pass.</summary>
[Collection(RegistryCollection.Name)]
public sealed class EdgeGateTests : IAsyncLifetime
{
    private readonly List<EdgeRig> _rigs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var rig in _rigs)
        {
            await rig.DisposeAsync();
        }

        CampgroundLifetime.ResetForTests();
    }

    private async Task<EdgeRig> Rig(string[]? knownProxies = null)
    {
        var rig = await EdgeRig.StartAsync(knownProxies);
        _rigs.Add(rig);
        return rig;
    }

    private static async Task AssertRefusedAsync(EdgeRig rig, string source = "127.0.0.5")
    {
        var before = rig.Jellyfin.All.Count(s => s.PathAndQuery == "/echo");
        using var client = rig.Client(source);
        using var response = await client.Http.GetAsync("echo");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Equal(UiText.English("edge.gate.closed"), await response.Content.ReadAsStringAsync());
        Assert.Equal(before, rig.Jellyfin.All.Count(s => s.PathAndQuery == "/echo"));
    }

    [Fact]
    public async Task With_trust_and_a_passing_self_test_the_gate_opens()
    {
        var rig = await Rig();

        var edge = await rig.StartEdgeAsync();

        Assert.True(edge.Gate.IsOpen, edge.Gate.Reason);
        Assert.Equal(GateReasons.Open, edge.Gate.Reason);
        using var client = rig.Client();
        using var ok = await client.Http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Jellyfin_started_with_no_Known_proxies_refuses_every_client_even_from_loopback()
    {
        // Jellyfin's live options are off (ApiServiceCollectionExtensions.cs:171-176), so it would see Edge's address for everyone.
        var rig = await Rig(Array.Empty<string>());

        var edge = await rig.StartEdgeAsync();

        Assert.False(edge.Gate.IsOpen);
        Assert.Equal(GateReasons.NotTrusted, edge.Gate.Reason);
        await AssertRefusedAsync(rig, "127.0.0.5");
        await AssertRefusedAsync(rig, "127.0.0.1");

        // Shut from the start is said once, with the reason, so an admin isn't left with a silent 503.
        await edge.CheckGateAsync(CancellationToken.None);
        Assert.Single(rig.Log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Text.Contains(GateReasons.NotTrusted, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listening_on_every_address_the_default_the_gate_still_opens()
    {
        // One dual-mode socket: the self-test arrives as ::ffff:127.0.0.2 (the first ts1 run stayed shut on this).
        var rig = await Rig();

        var edge = await rig.StartEdgeAsync(rig.Options(anyAddress: true));

        Assert.True(edge.Gate.IsOpen, edge.Gate.Reason);
        using var client = rig.Client("127.0.0.5");
        using var ok = await client.Http.GetAsync("echo");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task A_forged_probe_header_doesnt_get_past_a_shut_gate()
    {
        // The probe skips the gate, so only this start's token may: a guessed one is refused like any other client.
        var rig = await Rig(Array.Empty<string>());
        var edge = await rig.StartEdgeAsync();
        Assert.False(edge.Gate.IsOpen);
        var before = rig.Jellyfin.All.Count;

        foreach (var forged in new[] { "forged", string.Empty, rig.Identity.ProbeToken + "x" })
        {
            using var client = rig.Client("127.0.0.5");
            using var request = new HttpRequestMessage(HttpMethod.Get, "echo");
            request.Headers.TryAddWithoutValidation("X-Trailhead-Probe", forged);
            using var response = await client.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.Equal(before, rig.Jellyfin.All.Count);
    }

    [Fact]
    public async Task Known_proxies_written_while_Jellyfin_runs_dont_open_the_gate_until_a_restart()
    {
        // Bonfire's test (03 §7 T-S1 item 5). The saved setting says "trusted" from the moment it is written; a gate that
        // read it would open here, while Jellyfin still sees Edge's address for every client.
        var rig = await Rig(Array.Empty<string>());
        var saved = new List<string>();
        rig.Live = () => rig.Jellyfin.Options;
        var edge = await rig.StartEdgeAsync();
        Assert.False(edge.Gate.IsOpen);

        saved.Add("127.0.0.1");
        var after = await edge.CheckGateAsync(CancellationToken.None);

        Assert.False(after.IsOpen);
        Assert.Equal(GateReasons.NotTrusted, after.Reason);
        await AssertRefusedAsync(rig);

        // The restart: Jellyfin builds its options from the saved list (a new stand-in), and Edge starts again.
        await rig.StopEdgeAsync();
        var restarted = await Rig(saved.ToArray());
        var again = await restarted.StartEdgeAsync();
        Assert.True(again.Gate.IsOpen, again.Gate.Reason);
    }

    [Fact]
    public async Task Options_that_claim_trust_while_Jellyfin_doesnt_apply_it_fail_the_self_test()
    {
        // The live options are the only proof of trust Edge can read; the self-test catches a Jellyfin that ignores them.
        var rig = await Rig(Array.Empty<string>());
        var claimed = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
        claimed.KnownProxies.Add(IPAddress.Loopback);
        rig.Live = () => claimed;

        var edge = await rig.StartEdgeAsync();

        Assert.False(edge.Gate.IsOpen);
        Assert.Equal(GateReasons.AddressNotPassed, edge.Gate.Reason);
        await AssertRefusedAsync(rig);
    }

    [Fact]
    public async Task A_forwarding_address_that_changes_after_the_gate_opened_closes_it_again()
    {
        var rig = await Rig();
        var edge = await rig.StartEdgeAsync();
        Assert.True(edge.Gate.IsOpen, edge.Gate.Reason);

        rig.Forwarding = IPAddress.Parse("192.0.2.10");
        var after = await edge.CheckGateAsync(CancellationToken.None);

        Assert.False(after.IsOpen);
        Assert.Equal(GateReasons.ForwardingChanged, after.Reason);
        await AssertRefusedAsync(rig);
    }

    [Fact]
    public async Task Trust_removed_from_the_live_options_after_the_gate_opened_closes_it_again()
    {
        var rig = await Rig();
        var edge = await rig.StartEdgeAsync();
        Assert.True(edge.Gate.IsOpen, edge.Gate.Reason);

        rig.Jellyfin.Options.KnownProxies.Clear();
        rig.Jellyfin.Options.KnownIPNetworks.Clear();
        var after = await edge.CheckGateAsync(CancellationToken.None);

        Assert.False(after.IsOpen);
        Assert.Equal(GateReasons.NotTrusted, after.Reason);
        await AssertRefusedAsync(rig);
    }

    [Fact]
    public async Task A_Jellyfin_that_answers_with_another_version_doesnt_pass()
    {
        var rig = await Rig();
        var test = new SelfTest(() => new IPEndPoint(IPAddress.Loopback, rig.Port), IPAddress.Parse("127.0.0.2"), rig.Identity.ProbeToken, string.Empty, () => rig.Certificate!.Leaf.Thumbprint, "10.11.0", TimeSpan.FromSeconds(5));
        await rig.StartEdgeAsync();
        var result = await test.RunAsync(CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(GateReasons.NoJellyfin, result.Reason);
    }

    [Fact]
    public async Task The_self_test_given_nothing_to_talk_to_fails()
    {
        // The harness's empty input: no Edge on the port. It must fail, never pass.
        var port = FreePorts.Take(1)[0];
        var test = new SelfTest(() => new IPEndPoint(IPAddress.Loopback, port), IPAddress.Parse("127.0.0.2"), "token", string.Empty, () => null, null, TimeSpan.FromSeconds(2));

        var result = await test.RunAsync(CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(GateReasons.SelfTestFailed, result.Reason);
    }

    [Fact]
    public void Live_trust_reads_known_proxies_and_networks_and_nothing_when_forwarding_is_off()
    {
        var off = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.None };
        off.KnownProxies.Add(IPAddress.Loopback);
        var byNetwork = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
        byNetwork.KnownProxies.Clear();
        byNetwork.KnownIPNetworks.Clear();
        byNetwork.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.1.0/24"));

        Assert.False(LiveTrust.Trusts(off, IPAddress.Loopback));
        Assert.False(LiveTrust.Trusts(null, IPAddress.Loopback));
        Assert.True(LiveTrust.Trusts(byNetwork, IPAddress.Parse("192.168.1.10")));
        Assert.False(LiveTrust.Trusts(byNetwork, IPAddress.Parse("192.168.2.10")));
    }
}

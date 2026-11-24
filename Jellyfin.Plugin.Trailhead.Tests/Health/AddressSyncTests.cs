using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Network;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

internal sealed class FixedLookup : IPublicAddressLookup
{
    internal IPAddress? V4 { get; set; }

    internal IPAddress? V6 { get; set; }

    internal int Calls { get; private set; }

    public Task<PublicAddresses> LookupAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new PublicAddresses(V4, V6));
    }
}

/// <summary>The address sync against FakeDynu on a real socket, through the real DynuClient.</summary>
public sealed class AddressSyncTests : IAsyncLifetime
{
    private const string Name = "trailhead-test.freeddns.org";
    private static readonly IPAddress Ours = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Old = IPAddress.Parse("198.51.100.20");

    private readonly TempFolder _folder = new();
    private readonly ListLogger _log = new();
    private readonly FixedLookup _lookup = new() { V4 = Ours };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly PublicAddressWatch _watch = new();
    private FakeDynu _dynu = null!;
    private long _id;

    internal PluginConfiguration Config { get; } = new() { Hostname = Name, DynuKey = "sealed" };

    public async Task InitializeAsync()
    {
        _dynu = await FakeDynu.StartAsync();
        _id = _dynu.AddDomain(Name);
    }

    public async Task DisposeAsync()
    {
        await _dynu.DisposeAsync();
        _folder.Dispose();
    }

    private AddressSync Sync(string? key = FakeDynu.Key) => new(
        () => Config,
        sealedKey => sealedKey == "sealed" ? key : null,
        k => DynuClient.Create(k, _log, TimeProvider.System, _dynu.BaseUrl, new DynuClientOptions { MaxAttempts = 2, BackoffBase = TimeSpan.FromMilliseconds(1), RequestTimeout = TimeSpan.FromSeconds(10) }),
        _lookup,
        _folder.File("address.json"),
        _watch,
        new SemaphoreSlim(1, 1),
        _time,
        _log);

    private void Hold(IPAddress? v4)
    {
        _dynu.SetHeld(_id, "ipv4", v4 is not null);
        _dynu.SetHeld(_id, "ipv4Address", v4?.ToString());
    }

    private string HeldV4 => _dynu.Held(_id)["ipv4Address"]?.GetValue<string>() ?? string.Empty;

    [Fact]
    public async Task Nothing_set_up_means_no_lookup_and_no_call_to_Dynu()
    {
        Config.Hostname = string.Empty;

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.NotSetUp, status.Outcome);
        Assert.Equal(0, _lookup.Calls);
        Assert.Empty(_dynu.Requests);
        Assert.False(status.IsFault(_time.GetUtcNow()));
    }

    [Fact]
    public async Task A_lookup_that_finds_nothing_fails_rather_than_passing()
    {
        _lookup.V4 = null;

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.NoPublicAddress, status.Outcome);
        Assert.True(status.Failing);
        Assert.Empty(_dynu.Updates);
    }

    [Fact]
    public async Task When_Dynu_already_has_our_address_nothing_is_sent()
    {
        Hold(Ours);

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.Unchanged, status.Outcome);
        Assert.Empty(_dynu.Updates);

        // It was asked, by id: GET /v2/dns/{id}, not the list's copy (A3).
        Assert.Contains(_dynu.Requests, r => r.Method == "GET" && r.Path == "/v2/dns/" + _id);
    }

    [Fact]
    public async Task When_Dynu_has_another_address_it_is_updated_with_every_field_as_Dynu_held_it()
    {
        Hold(Old);
        _dynu.SetHeld(_id, "group", "home");
        _dynu.SetHeld(_id, "ttl", 300);
        _dynu.SetHeld(_id, "ipv4WildcardAlias", true);
        var before = _dynu.Held(_id);

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.Updated, status.Outcome);
        Assert.Equal(Ours.ToString(), HeldV4);
        var sent = (JsonObject)JsonNode.Parse(Assert.Single(_dynu.Updates))!;
        foreach (var field in DynuHeldRecord.Writable)
        {
            Assert.True(sent.ContainsKey(field), "the update left out " + field);
            if (field is not ("ipv4Address" or "ipv4"))
            {
                Assert.True(JsonNode.DeepEquals(before[field], sent[field]), field + " was changed");
            }
        }

        Assert.DoesNotContain("token", sent.Select(p => p.Key));
        Assert.DoesNotContain(FakeDynu.Token, _dynu.Updates.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_turned_off_with_our_address_in_it_is_turned_back_on()
    {
        _dynu.SetHeld(_id, "ipv4", false);
        _dynu.SetHeld(_id, "ipv4Address", Ours.ToString());

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.Updated, status.Outcome);
        Assert.True(_dynu.Held(_id)["ipv4"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_comparison_is_with_what_Dynu_holds_so_a_change_made_behind_its_back_is_put_right()
    {
        Hold(Old);
        var sync = Sync();
        Assert.Equal(SyncOutcome.Updated, (await sync.RunAsync(CancellationToken.None)).Outcome);
        Assert.Equal(SyncOutcome.Unchanged, (await sync.RunAsync(CancellationToken.None)).Outcome);

        // Someone edits the record in Dynu's web panel. A cached "last sent" would say nothing changed.
        Hold(Old);
        var third = await sync.RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.Updated, third.Outcome);
        Assert.Equal(Ours.ToString(), HeldV4);
        Assert.Equal(2, _dynu.Updates.Count);
    }

    [Fact]
    public async Task No_IPv6_address_is_ever_sent_and_an_AAAA_the_admin_set_stays()
    {
        _lookup.V6 = IPAddress.Parse("2001:db8::77");
        Hold(Old);
        _dynu.SetHeld(_id, "ipv6", true);
        _dynu.SetHeld(_id, "ipv6Address", "2001:db8::1");

        await Sync().RunAsync(CancellationToken.None);

        var body = Assert.Single(_dynu.Updates);
        Assert.DoesNotContain("2001:db8::77", body, StringComparison.OrdinalIgnoreCase);
        var held = _dynu.Held(_id);
        Assert.Equal("2001:db8::1", held["ipv6Address"]!.GetValue<string>());
        Assert.True(held["ipv6"]!.GetValue<bool>());
    }

    [Fact]
    public async Task No_IPv6_switch_is_turned_on_when_the_record_had_none()
    {
        _lookup.V6 = IPAddress.Parse("2001:db8::77");
        Hold(Old);

        await Sync().RunAsync(CancellationToken.None);

        var held = _dynu.Held(_id);
        Assert.False(held["ipv6"]!.GetValue<bool>());
        Assert.Null(held["ipv6Address"]);
    }

    [Fact]
    public async Task The_client_refuses_to_send_an_IPv6_address_as_the_A_record()
    {
        using var client = DynuClient.Create(FakeDynu.Key, _log, TimeProvider.System, _dynu.BaseUrl);
        var held = await client.GetDomainAsync(_id, CancellationToken.None);

        var e = await Assert.ThrowsAsync<DynuException>(() => client.UpdateIPv4Async(held, IPAddress.Parse("2001:db8::77"), CancellationToken.None));

        Assert.Equal(Jellyfin.Plugin.Trailhead.Acme.FailureKind.Permanent, e.Kind);
        Assert.Empty(_dynu.Updates);
    }

    [Fact]
    public async Task FakeDynu_refuses_an_update_sent_chunked_as_live_Dynu_does_and_the_client_sends_a_length()
    {
        // The harness first: a chunked body (no Content-Length) gets 501, as live Dynu answered on 2026-11-16.
        using var raw = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_dynu.BaseUrl, "dns/" + _id))
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"" + Name + "\",\"ipv4\":true,\"ipv4Address\":\"203.0.113.9\"}"))),
        };
        request.Headers.Add("API-Key", FakeDynu.Key);
        request.Headers.TransferEncodingChunked = true;
        using (var response = await raw.SendAsync(request))
        {
            Assert.Contains("501", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.NotEqual("203.0.113.9", HeldV4);

        // Then the sync's own update gets through.
        Hold(Old);
        Assert.Equal(SyncOutcome.Updated, (await Sync().RunAsync(CancellationToken.None)).Outcome);
        Assert.Equal(Ours.ToString(), HeldV4);
    }

    [Fact]
    public async Task A_name_gone_from_the_account_is_a_fault_at_once_and_nothing_is_sent()
    {
        Config.Hostname = "someone-else.freeddns.org";

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.NameGone, status.Outcome);
        Assert.True(status.IsFault(_time.GetUtcNow()));
        Assert.Empty(_dynu.Updates);
    }

    [Fact]
    public async Task A_key_Dynu_refuses_is_a_fault_at_once_and_never_appears_in_the_log()
    {
        var status = await Sync(key: "WRONG-KEY-do-not-use").RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.KeyRejected, status.Outcome);
        Assert.True(status.IsFault(_time.GetUtcNow()));
        Assert.DoesNotContain("WRONG-KEY", _log.All, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeDynu.Token, _log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_that_will_not_open_is_a_fault_without_any_call()
    {
        var status = await Sync(key: null).RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.KeyRejected, status.Outcome);
        Assert.Empty(_dynu.Requests);
    }

    [Fact]
    public async Task A_passing_failure_counts_as_a_fault_only_after_an_hour_and_the_clock_survives_a_restart()
    {
        _lookup.V4 = null;
        var first = await Sync().RunAsync(CancellationToken.None);
        Assert.False(first.IsFault(_time.GetUtcNow()));

        _time.Advance(TimeSpan.FromMinutes(59));
        var restarted = Sync();
        var second = await restarted.RunAsync(CancellationToken.None);
        Assert.Equal(first.FailingSince, second.FailingSince);
        Assert.False(second.IsFault(_time.GetUtcNow()));

        _time.Advance(TimeSpan.FromMinutes(1));
        var third = await restarted.RunAsync(CancellationToken.None);
        Assert.True(third.IsFault(_time.GetUtcNow()));

        // Working again clears the clock.
        _lookup.V4 = Ours;
        Hold(Ours);
        var fixedNow = await restarted.RunAsync(CancellationToken.None);
        Assert.Null(fixedNow.FailingSince);
        Assert.False(fixedNow.IsFault(_time.GetUtcNow()));
    }

    [Fact]
    public async Task Dynu_down_is_transient_with_grace()
    {
        for (var i = 0; i < 4; i++)
        {
            _dynu.Enqueue(new FakeDynuCanned(503, "busy"));
        }

        var status = await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(SyncOutcome.DynuFailed, status.Outcome);
        Assert.False(status.Permanent);
        Assert.False(status.IsFault(_time.GetUtcNow()));
    }

    [Fact]
    public async Task The_sync_tells_the_watch_our_public_address()
    {
        Hold(Ours);

        await Sync().RunAsync(CancellationToken.None);

        Assert.Equal(Ours, _watch.Own);
    }
}

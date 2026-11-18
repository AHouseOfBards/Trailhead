using System.Net;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

[Collection(RegistryCollection.Name)]
public sealed class SetupNameTests : ApiTestBase
{
    private const string Claim = "/Trailhead/name/claim";
    private const string Check = "/Trailhead/name/check";

    private Task<HttpResponseMessage> ClaimAsync(string label, string parent = "freeddns.org") => PostAsync(Claim, new { Label = label, Parent = parent });

    private Task<HttpResponseMessage> CheckAsync(string label, string parent = "freeddns.org") => PostAsync(Check, new { Label = label, Parent = parent });

    private static async Task<string> KindAsync(HttpResponseMessage response)
    {
        using var json = await JsonAsync(response);
        return json.RootElement.GetProperty("Kind").GetString()!;
    }

    // ---- check

    [Fact]
    public async Task A_name_not_on_the_account_checks_as_Unchecked_and_nothing_is_created_or_saved()
    {
        SeedKey();

        using var response = await CheckAsync("MyMedia");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Unchecked", json.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("mymedia.freeddns.org", json.RootElement.GetProperty("Name").GetString());
        Assert.Equal(0, DynuCount("POST", "/v2/dns"));
        Assert.Equal(string.Empty, Config.Hostname);
        Assert.Equal(string.Empty, Config.PendingName);
    }

    [Fact]
    public async Task A_name_on_the_account_checks_as_Ours()
    {
        SeedKey();
        Dynu.AddDomain("mymedia.freeddns.org");

        using var response = await CheckAsync("mymedia");

        Assert.Equal("Ours", await KindAsync(response));
        Assert.Equal(string.Empty, Config.Hostname);
    }

    [Theory]
    [InlineData("ab", "freeddns.org", "TooShort")]
    [InlineData("a.b.c", "freeddns.org", "HasDot")]
    [InlineData("", "freeddns.org", "Empty")]
    [InlineData("mymedia", "example.com", "ParentNotOffered")]
    [InlineData("mymedia", "dynu.com", "ParentBlocked")]
    public async Task A_name_that_breaks_a_rule_is_Invalid_with_the_rule_and_makes_no_call(string label, string parent, string problem)
    {
        SeedKey();

        using var response = await CheckAsync(label, parent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Invalid", json.RootElement.GetProperty("Kind").GetString());
        Assert.Equal(problem, json.RootElement.GetProperty("Problem").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("Message").GetString()));
        Assert.Equal(0, DynuCalls);
    }

    [Fact]
    public async Task A_check_with_no_saved_key_is_409_dynu_key_missing()
    {
        using var response = await CheckAsync("mymedia");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("dynu_key_missing", Code(response));
        Assert.Equal(0, DynuCalls);
    }

    [Fact]
    public async Task A_saved_key_Dynu_no_longer_accepts_is_409_dynu_key_rejected_on_a_check_and_a_claim()
    {
        SeedKey("a-key-that-was-revoked");

        using var check = await CheckAsync("mymedia");
        using var claim = await ClaimAsync("mymedia");

        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
        Assert.Equal("dynu_key_rejected", Code(check));
        Assert.Equal(HttpStatusCode.Conflict, claim.StatusCode);
        Assert.Equal("dynu_key_rejected", Code(claim));
        Assert.Equal(string.Empty, Config.Hostname);
        Assert.Equal(string.Empty, Config.PendingName);
    }

    [Fact]
    public async Task A_saved_key_from_another_machine_counts_as_no_key()
    {
        ConfigGate.Update(c => c.DynuKey = new DynuKey(RenewalTestKit_Box(9)).Seal(Sentinel));

        using var response = await ClaimAsync("mymedia");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("dynu_key_missing", Code(response));
        Assert.Equal(0, DynuCalls);
    }

    private static Campground.Shared.Security.SecretBox RenewalTestKit_Box(byte seed) => Renewal.RenewalTestKit.Box(seed);

    // ---- claim

    [Fact]
    public async Task A_free_name_is_Claimed_saved_and_recorded_as_made_by_Trailhead()
    {
        SeedKey();

        using var response = await ClaimAsync("MyMedia");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Claimed", json.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("mymedia.freeddns.org", json.RootElement.GetProperty("Name").GetString());
        Assert.Equal("mymedia.freeddns.org", json.RootElement.GetProperty("Setup").GetProperty("Hostname").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("Alternatives").GetArrayLength());
        var saved = ThePlugin.ReadSavedConfiguration();
        Assert.Equal("mymedia.freeddns.org", saved.Hostname);
        Assert.Equal("mymedia.freeddns.org", saved.NameCreatedByTrailhead);
        Assert.Equal(string.Empty, saved.PendingName);
        Assert.Equal("mymedia.freeddns.org", Assert.Single(Dynu.DomainNames));
    }

    [Fact]
    public async Task A_name_already_on_the_account_is_Ours_and_saved_but_not_recorded_as_made_by_Trailhead()
    {
        SeedKey();
        Dynu.AddDomain("mymedia.freeddns.org");

        using var response = await ClaimAsync("mymedia");

        Assert.Equal("Ours", await KindAsync(response));
        var saved = ThePlugin.ReadSavedConfiguration();
        Assert.Equal("mymedia.freeddns.org", saved.Hostname);
        Assert.Equal(string.Empty, saved.NameCreatedByTrailhead);
        Assert.Equal(string.Empty, saved.PendingName);
        Assert.Equal(0, DynuCount("POST", "/v2/dns"));
    }

    [Fact]
    public async Task A_taken_name_is_offered_under_the_next_three_endings_and_saves_nothing()
    {
        SeedKey();
        Dynu.AddForeignDomain("www.freeddns.org");

        using var response = await ClaimAsync("www");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("Taken", json.RootElement.GetProperty("Kind").GetString());
        var offered = json.RootElement.GetProperty("Alternatives").EnumerateArray().Select(a => a.GetString()).ToList();
        var expected = ParentDomains.Embedded().Others("freeddns.org").Take(3).Select(p => "www." + p.Name).ToList();
        Assert.Equal(3, expected.Count);
        Assert.Equal(expected, offered);
        Assert.Equal(string.Empty, Config.Hostname);
        Assert.Equal(string.Empty, Config.PendingName);
        Assert.Empty(Dynu.DomainNames);
    }

    [Fact]
    public async Task A_full_account_is_AccountFull()
    {
        SeedKey();
        Dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(503, "Validation Exception")));

        using var response = await ClaimAsync("mymedia");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AccountFull", await KindAsync(response));
        Assert.Equal(string.Empty, Config.Hostname);
        Assert.Equal(string.Empty, Config.PendingName);
    }

    [Fact]
    public async Task Dynu_refusing_the_create_is_DynuRefused_and_a_name_the_owner_then_adds_by_hand_is_Ours_and_not_Trailheads()
    {
        SeedKey();
        Dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(501, "Argument Exception")));

        using var refused = await ClaimAsync("mymedia");
        Dynu.AddDomain("mymedia.freeddns.org");
        using var again = await ClaimAsync("mymedia");

        Assert.Equal("DynuRefused", await KindAsync(refused));
        Assert.Equal("Ours", await KindAsync(again));
        var saved = ThePlugin.ReadSavedConfiguration();
        Assert.Equal("mymedia.freeddns.org", saved.Hostname);
        Assert.Equal(string.Empty, saved.NameCreatedByTrailhead);
        Assert.Equal(string.Empty, saved.PendingName);
    }

    [Fact]
    public async Task Dynu_not_answering_is_503_and_the_name_stays_recorded_as_in_doubt()
    {
        SeedKey();
        Dynu.EnqueueCreate(new FakeDynuCanned(500, "{}"));
        Dynu.EnqueueCreate(new FakeDynuCanned(500, "{}"));

        using var response = await ClaimAsync("mymedia");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dynu_unavailable", Code(response));
        Assert.True(response.Headers.RetryAfter is not null);
        Assert.Equal("mymedia.freeddns.org", Config.PendingName);
        Assert.Equal(string.Empty, Config.Hostname);
    }

    [Fact]
    public async Task A_name_left_in_doubt_that_turns_out_to_exist_is_recorded_as_made_by_Trailhead()
    {
        SeedKey();
        Dynu.EnqueueCreate(new FakeDynuCanned(500, "{}"));
        Dynu.EnqueueCreate(new FakeDynuCanned(500, "{}"));
        using var lost = await ClaimAsync("mymedia");

        // The create had gone through after all.
        Dynu.AddDomain("mymedia.freeddns.org");
        using var again = await ClaimAsync("mymedia");

        Assert.Equal("Ours", await KindAsync(again));
        var saved = ThePlugin.ReadSavedConfiguration();
        Assert.Equal("mymedia.freeddns.org", saved.NameCreatedByTrailhead);
        Assert.Equal(string.Empty, saved.PendingName);
    }

    [Fact]
    public async Task Claiming_again_makes_one_name()
    {
        SeedKey();

        using var first = await ClaimAsync("mymedia");
        using var second = await ClaimAsync("mymedia");

        Assert.Equal("Claimed", await KindAsync(first));
        Assert.Equal("Ours", await KindAsync(second));
        Assert.Single(Dynu.DomainNames);
        Assert.Equal(1, DynuCount("POST", "/v2/dns"));
        Assert.Equal("mymedia.freeddns.org", Config.NameCreatedByTrailhead);
    }

    [Fact]
    public async Task Parallel_claims_make_one_name_and_every_caller_gets_it()
    {
        SeedKey();

        // A window between the list and the create, so two claims without a lock both see the name free.
        Dynu.ListDelay = TimeSpan.FromMilliseconds(60);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ClaimAsync("mymedia")));

        var kinds = new List<string>();
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            kinds.Add(await KindAsync(r));
            r.Dispose();
        }

        Assert.Equal(1, kinds.Count(k => k == "Claimed"));
        Assert.Equal(7, kinds.Count(k => k == "Ours"));
        Assert.Single(Dynu.DomainNames);
        Assert.Equal(1, DynuCount("POST", "/v2/dns"));
    }

    [Fact]
    public async Task Choosing_another_name_replaces_the_saved_one_and_deletes_nothing_on_Dynu()
    {
        SeedKey();
        using var first = await ClaimAsync("firstname");
        using var second = await ClaimAsync("secondname");

        Assert.Equal("secondname.freeddns.org", Config.Hostname);
        Assert.Equal("secondname.freeddns.org", Config.NameCreatedByTrailhead);
        Assert.Equal(2, Dynu.DomainNames.Count);
        Assert.Equal(0, Dynu.Requests.Count(r => r.Method == "DELETE"));
    }

    [Fact]
    public async Task A_name_that_breaks_a_rule_is_Invalid_on_a_claim_too_with_the_state_attached_and_no_call()
    {
        SeedKey();

        using var response = await ClaimAsync("ab");

        using var json = await JsonAsync(response);
        Assert.Equal("Invalid", json.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("TooShort", json.RootElement.GetProperty("Problem").GetString());
        Assert.Equal("Saved", json.RootElement.GetProperty("Setup").GetProperty("DynuKey").GetString());
        Assert.Equal(0, DynuCalls);
        Assert.Equal(string.Empty, Config.PendingName);
    }

    [Fact]
    public async Task A_claim_with_no_saved_key_is_409_dynu_key_missing()
    {
        using var response = await ClaimAsync("mymedia");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("dynu_key_missing", Code(response));
        Assert.Equal(0, DynuCalls);
        Assert.Equal(string.Empty, Config.PendingName);
    }

    [Fact]
    public async Task A_name_is_never_written_to_the_log()
    {
        SeedKey();
        Dynu.AddForeignDomain("secretlabel.freeddns.org");

        (await ClaimAsync("secretlabel")).Dispose();
        (await ClaimAsync("anotherlabel")).Dispose();
        (await CheckAsync("thirdlabel")).Dispose();

        Assert.DoesNotContain("secretlabel", Log.All);
        Assert.DoesNotContain("anotherlabel", Log.All);
        Assert.DoesNotContain("thirdlabel", Log.All);
    }

    [Fact]
    public async Task A_failed_read_back_on_the_name_is_a_500_and_not_a_200()
    {
        SeedKey();
        Xml.Current = FlakyXml.Mode.WritesBlank;

        using var response = await ClaimAsync("mymedia");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("save_failed", Code(response));
        Assert.Equal(0, DynuCount("POST", "/v2/dns"));
    }
}

/// <summary>The name is written down before the create goes out, so a lost answer leaves a record.</summary>
[Collection(RegistryCollection.Name)]
public sealed class SetupNameRecordTests : ApiTestBase
{
    internal override DynuClientOptions DynuOptions => new() { MaxAttempts = 2, BackoffBase = TimeSpan.FromMilliseconds(1), RequestTimeout = TimeSpan.FromMilliseconds(600) };

    [Fact]
    public async Task The_name_is_recorded_as_pending_before_Dynu_is_asked_to_create_it()
    {
        SeedKey();

        // The first call of a claim stalls, so the claim is still in flight while the test looks.
        Dynu.Enqueue(new FakeDynuCanned(200, "{}", StallBody: true));
        var claim = PostAsync("/Trailhead/name/claim", new { Label = "mymedia", Parent = "freeddns.org" });
        Assert.True(await WaitUntilAsync(() => DynuCalls >= 1), "the claim never reached Dynu");
        var duringTheCall = Config.PendingName;
        using var response = await claim;

        Assert.Equal("mymedia.freeddns.org", duringTheCall);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, Config.PendingName);
        Assert.Equal("mymedia.freeddns.org", Config.NameCreatedByTrailhead);
    }
}

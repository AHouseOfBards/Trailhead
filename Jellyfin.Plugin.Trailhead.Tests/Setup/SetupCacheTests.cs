using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>The account's name list is cached for 30 seconds for checks only. A claim and a new key clear it.</summary>
[Collection(RegistryCollection.Name)]
public sealed class SetupCacheTests : ApiTestBase
{
    internal override bool UseFakeTime => true;

    private async Task<string> CheckKindAsync(string label)
    {
        using var response = await PostAsync("/Trailhead/name/check", new { Label = label, Parent = "freeddns.org" });
        using var json = await JsonAsync(response);
        return json.RootElement.GetProperty("Kind").GetString()!;
    }

    private int Lists => DynuCount("GET", "/v2/dns");

    [Fact]
    public async Task A_second_check_within_30_seconds_does_not_ask_Dynu_again_and_one_after_does()
    {
        SeedKey();

        Assert.Equal("Unchecked", await CheckKindAsync("mymedia"));
        Assert.Equal(1, Lists);

        // Added outside Trailhead: a cached list can't know yet.
        Dynu.AddDomain("mymedia.freeddns.org");
        Assert.Equal("Unchecked", await CheckKindAsync("mymedia"));
        Assert.Equal(1, Lists);

        Fake!.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("Ours", await CheckKindAsync("mymedia"));
        Assert.Equal(2, Lists);
    }

    [Fact]
    public async Task A_claim_always_asks_Dynu_fresh_and_clears_the_cache_for_the_checks_after_it()
    {
        SeedKey();
        Assert.Equal("Unchecked", await CheckKindAsync("other"));
        var listsBefore = Lists;

        // Cached list has no "mymedia"; a claim must not trust it.
        Dynu.AddDomain("mymedia.freeddns.org");
        using var claim = await PostAsync("/Trailhead/name/claim", new { Label = "mymedia", Parent = "freeddns.org" });
        using var json = await JsonAsync(claim);
        Assert.Equal("Ours", json.RootElement.GetProperty("Kind").GetString());
        Assert.True(Lists > listsBefore);

        Assert.Equal("Ours", await CheckKindAsync("mymedia"));
    }

    [Fact]
    public async Task A_new_key_clears_the_cache()
    {
        SeedKey();
        Assert.Equal("Unchecked", await CheckKindAsync("mymedia"));
        Dynu.AddDomain("mymedia.freeddns.org");

        using var save = await PostAsync("/Trailhead/dynu-key", new { Key = Sentinel });
        Assert.True(save.IsSuccessStatusCode);

        Assert.Equal("Ours", await CheckKindAsync("mymedia"));
    }
}

/// <summary>One setup request has a time limit in all, however many Dynu calls it makes.</summary>
[Collection(RegistryCollection.Name)]
public sealed class SetupBudgetTests : ApiTestBase
{
    internal override TimeSpan? Budget => TimeSpan.FromMilliseconds(400);

    // Longer than the budget, so only the budget can end the call in time.
    internal override Jellyfin.Plugin.Trailhead.Dns.DynuClientOptions DynuOptions => new() { MaxAttempts = 2, RequestTimeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task A_stalled_Dynu_ends_in_503_when_the_budget_is_spent_and_nothing_is_saved()
    {
        Dynu.Enqueue(new TestSupport.FakeDynuCanned(200, "{}", StallBody: true));
        var started = DateTime.UtcNow;

        using var response = await PostAsync("/Trailhead/dynu-key", new { Key = Sentinel });

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dynu_unavailable", Code(response));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the budget did not end the call");
        Assert.Equal(string.Empty, Config.DynuKey);
    }

    [Fact]
    public async Task A_stalled_claim_ends_in_503_and_keeps_the_name_recorded_as_in_doubt()
    {
        SeedKey();
        Dynu.Enqueue(new TestSupport.FakeDynuCanned(200, "{}", StallBody: true));

        using var response = await PostAsync("/Trailhead/name/claim", new { Label = "mymedia", Parent = "freeddns.org" });

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("mymedia.freeddns.org", Config.PendingName);
    }
}

using System.Net;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

[Collection(RegistryCollection.Name)]
public sealed class SetupKeyTests : ApiTestBase
{
    private const string Route = "/Trailhead/dynu-key";

    private Task<HttpResponseMessage> SaveKey(string? key) => PostAsync(Route, new { Key = key });

    [Fact]
    public async Task An_accepted_key_is_sealed_saved_and_reads_back_to_the_same_key()
    {
        using var response = await SaveKey(Sentinel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.False(json.RootElement.GetProperty("NameCleared").GetBoolean());
        Assert.Equal("Saved", json.RootElement.GetProperty("Setup").GetProperty("DynuKey").GetString());
        Assert.StartsWith("cg1:", Config.DynuKey);
        Assert.StartsWith("cg1:", ThePlugin.ReadSavedConfiguration().DynuKey);
        Assert.DoesNotContain(Sentinel, SavedXml);
        Assert.True(new DynuKey(Box).TryOpen(ThePlugin.ReadSavedConfiguration().DynuKey, out var opened));
        Assert.Equal(Sentinel, opened);
        Assert.Equal(1, DynuCount("GET", "/v2/dns"));
    }

    [Fact]
    public async Task Spaces_around_a_pasted_key_are_trimmed()
    {
        using var response = await SaveKey("  " + Sentinel + "\n");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(new DynuKey(Box).TryOpen(Config.DynuKey, out var opened));
        Assert.Equal(Sentinel, opened);
    }

    [Fact]
    public async Task A_key_Dynu_refuses_with_401_is_not_saved()
    {
        using var response = await SaveKey("some-other-key-123");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dynu_key_rejected", Code(response));
        Assert.Equal(string.Empty, Config.DynuKey);
        Assert.DoesNotContain("cg1:", SavedXml);
    }

    [Fact]
    public async Task A_key_Dynu_refuses_with_403_is_not_saved()
    {
        Dynu.Enqueue(new FakeDynuCanned(403, FakeDynu.Envelope(403, "Authentication Exception")));

        using var response = await SaveKey(Sentinel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dynu_key_rejected", Code(response));
        Assert.Equal(string.Empty, Config.DynuKey);
    }

    [Fact]
    public async Task Dynu_answering_with_a_server_error_gives_503_with_Retry_After_and_saves_nothing()
    {
        Dynu.Enqueue(new FakeDynuCanned(503, "{}"));
        Dynu.Enqueue(new FakeDynuCanned(503, "{}"));

        using var response = await SaveKey(Sentinel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dynu_unavailable", Code(response));
        Assert.True(response.Headers.RetryAfter is not null);
        Assert.Equal(string.Empty, Config.DynuKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has a space")]
    [InlineData("tab\tinside")]
    [InlineData("line\nbreak")]
    [InlineData("nul\0inside")]
    [InlineData("cafékey")]
    [InlineData(null)]
    public async Task A_key_with_the_wrong_shape_is_refused_before_any_call_to_Dynu(string? key)
    {
        using var response = await SaveKey(key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("dynu_key_invalid", Code(response));
        Assert.Equal(0, DynuCalls);
        Assert.Equal(string.Empty, Config.DynuKey);
    }

    [Fact]
    public async Task A_key_over_the_length_limit_is_refused_and_one_at_the_limit_goes_to_Dynu()
    {
        using var tooLong = await SaveKey(new string('k', SetupService.MaxKeyLength + 1));
        var callsAfterTooLong = DynuCalls;
        using var atLimit = await SaveKey(new string('k', SetupService.MaxKeyLength));

        Assert.Equal("dynu_key_invalid", Code(tooLong));
        Assert.Equal(0, callsAfterTooLong);
        Assert.Equal("dynu_key_rejected", Code(atLimit));
        Assert.Equal(1, DynuCalls);
    }

    [Fact]
    public async Task A_new_key_on_an_account_that_lacks_the_saved_name_clears_the_name()
    {
        ConfigGate.Update(c =>
        {
            c.Hostname = "old.freeddns.org";
            c.NameCreatedByTrailhead = "old.freeddns.org";
            c.PendingName = "older.freeddns.org";
        });

        using var response = await SaveKey(Sentinel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.True(json.RootElement.GetProperty("NameCleared").GetBoolean());
        Assert.Equal(string.Empty, json.RootElement.GetProperty("Setup").GetProperty("Hostname").GetString());
        var saved = ThePlugin.ReadSavedConfiguration();
        Assert.Equal(string.Empty, saved.Hostname);
        Assert.Equal(string.Empty, saved.NameCreatedByTrailhead);
        Assert.Equal(string.Empty, saved.PendingName);
        Assert.StartsWith("cg1:", saved.DynuKey);
        Assert.Contains("cleared it", Log.All);
        Assert.DoesNotContain("old.freeddns.org", Log.All);
    }

    [Fact]
    public async Task A_new_key_on_an_account_that_has_the_saved_name_keeps_it()
    {
        Dynu.AddDomain("old.freeddns.org");
        ConfigGate.Update(c =>
        {
            c.Hostname = "old.freeddns.org";
            c.NameCreatedByTrailhead = "old.freeddns.org";
        });

        using var response = await SaveKey(Sentinel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.False(json.RootElement.GetProperty("NameCleared").GetBoolean());
        var saved = ThePlugin.ReadSavedConfiguration();
        Assert.Equal("old.freeddns.org", saved.Hostname);
        Assert.Equal("old.freeddns.org", saved.NameCreatedByTrailhead);
    }

    [Fact]
    public async Task A_rejected_key_leaves_a_saved_name_and_key_alone()
    {
        SeedKey();
        Dynu.AddDomain("old.freeddns.org");
        ConfigGate.Update(c => c.Hostname = "old.freeddns.org");
        var sealedBefore = Config.DynuKey;

        using var response = await SaveKey("some-other-key-123");

        Assert.Equal("dynu_key_rejected", Code(response));
        Assert.Equal(sealedBefore, Config.DynuKey);
        Assert.Equal("old.freeddns.org", Config.Hostname);
    }

    [Theory]
    [InlineData(FlakyXml.Mode.WritesBlank)]
    [InlineData(FlakyXml.Mode.ReadFails)]
    [InlineData(FlakyXml.Mode.WriteFails)]
    public async Task A_key_that_does_not_read_back_is_a_500_and_not_a_200(FlakyXml.Mode mode)
    {
        Xml.Current = mode;

        using var response = await SaveKey(Sentinel);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("save_failed", Code(response));
        Assert.Equal(string.Empty, Config.DynuKey);
    }
}

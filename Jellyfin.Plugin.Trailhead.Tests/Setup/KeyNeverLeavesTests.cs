using System.Net;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>
/// The Dynu key goes in a request body and into the sealed setting, and nowhere else: not an answer, a header, a log
/// line, an exception message or the saved file. Each scenario sends the fake key down a different path, then looks
/// for it everywhere.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class KeyNeverLeavesTests : ApiTestBase
{
    private readonly List<string> _seen = new();

    private async Task Keep(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        _seen.Add(await DumpAsync(response));
    }

    private static string Pad(int length) => new('x', length);

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected-by-dynu")]
    [InlineData("shape-refused-with-a-space")]
    [InlineData("shape-refused-non-ascii")]
    [InlineData("malformed-json")]
    [InlineData("oversized-body")]
    [InlineData("dynu-server-error-that-echoes-the-key")]
    [InlineData("dynu-answer-that-is-not-json")]
    [InlineData("dynu-401-that-echoes-the-key")]
    [InlineData("name-routes-and-state-with-a-saved-key")]
    public async Task The_key_is_nowhere_in_what_comes_back_what_is_logged_or_what_is_saved(string scenario)
    {
        switch (scenario)
        {
            case "accepted":
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel }));
                await Keep(GetAsync("/Trailhead/setup"));
                break;
            case "rejected-by-dynu":
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel + "-but-not-the-right-one" }));
                break;
            case "shape-refused-with-a-space":
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel + " plus more" }));
                break;
            case "shape-refused-non-ascii":
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel + "é" }));
                break;
            case "malformed-json":
                await Keep(PostAsync("/Trailhead/dynu-key", "{\"Key\":\"" + Sentinel));
                await Keep(PostAsync("/Trailhead/dynu-key", "[\"" + Sentinel + "\"]"));
                await Keep(PostAsync("/Trailhead/dynu-key", "{\"Key\":{\"" + Sentinel + "\":1}}"));
                break;
            case "oversized-body":
                await Keep(PostAsync("/Trailhead/dynu-key", "{\"Key\":\"" + Sentinel + "\",\"Pad\":\"" + Pad(6000) + "\"}"));
                break;
            case "dynu-server-error-that-echoes-the-key":
                Dynu.Enqueue(new FakeDynuCanned(500, "{\"statusCode\":500,\"type\":\"" + "Oops" + "\",\"message\":\"" + Sentinel + "\"}"));
                Dynu.Enqueue(new FakeDynuCanned(500, "{\"statusCode\":500,\"type\":\"" + "Oops" + "\",\"message\":\"" + Sentinel + "\"}"));
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel }));
                break;
            case "dynu-answer-that-is-not-json":
                Dynu.Enqueue(new FakeDynuCanned(200, "<html>" + Sentinel + "</html>"));
                Dynu.Enqueue(new FakeDynuCanned(200, "<html>" + Sentinel + "</html>"));
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel }));
                break;
            case "dynu-401-that-echoes-the-key":
                Dynu.Enqueue(new FakeDynuCanned(401, "{\"statusCode\":401,\"type\":\"Authentication Exception\",\"message\":\"" + Sentinel + " is not valid\"}"));
                await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel }));
                break;
            case "name-routes-and-state-with-a-saved-key":
                SeedKey();
                await Keep(GetAsync("/Trailhead/setup"));
                await Keep(PostAsync("/Trailhead/name/check", new { Label = "mymedia", Parent = "freeddns.org" }));
                await Keep(PostAsync("/Trailhead/name/claim", new { Label = "mymedia", Parent = "freeddns.org" }));
                Dynu.EnqueueCreate(new FakeDynuCanned(200, FakeDynu.Envelope(505, "Validation Exception", Sentinel)));
                await Keep(PostAsync("/Trailhead/name/claim", new { Label = "anothername", Parent = "freeddns.org" }));
                await Keep(GetAsync("/Trailhead/setup"));
                break;
            default:
                throw new InvalidOperationException("unknown scenario " + scenario);
        }

        // A scenario that sent nothing would pass the checks below.
        Assert.NotEmpty(_seen);
        Assert.All(_seen, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        foreach (var text in _seen)
        {
            Assert.DoesNotContain(Sentinel, text);
            Assert.DoesNotContain("cg1:", text);
        }

        Assert.NotEmpty(Log.Entries);
        Assert.DoesNotContain(Sentinel, Log.All);
        Assert.DoesNotContain("cg1:", Log.All);
        Assert.DoesNotContain(Sentinel, SavedXml);
    }

    [Fact]
    public async Task The_saved_file_holds_the_key_sealed_and_only_sealed()
    {
        await Keep(PostAsync("/Trailhead/dynu-key", new { Key = Sentinel }));

        var xml = SavedXml;
        Assert.Contains("<DynuKey>cg1:", xml);
        Assert.DoesNotContain(Sentinel, xml);
    }

    [Fact]
    public async Task A_key_in_the_query_string_is_not_read()
    {
        using var response = await SendAsync(HttpMethod.Post, "/Trailhead/dynu-key?Key=" + Sentinel + "&api_key=" + Sentinel, body: "{}");

        Assert.Equal("dynu_key_invalid", Code(response));
        Assert.Equal(0, DynuCalls);
        Assert.Equal(string.Empty, Config.DynuKey);
    }

    [Fact]
    public async Task The_setup_state_never_holds_the_key_sealed_or_plain()
    {
        SeedKey();

        using var response = await GetAsync("/Trailhead/setup");
        var all = await DumpAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("cg1:", all);
        Assert.DoesNotContain(Sentinel, all);
    }
}

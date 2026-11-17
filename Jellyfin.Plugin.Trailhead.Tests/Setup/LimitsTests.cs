using System.Net;
using System.Text;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>The rate limits and the body cap. The clock is fake, so a ten-minute window passes in no time.</summary>
[Collection(RegistryCollection.Name)]
public sealed class LimitsTests : ApiTestBase
{
    internal override bool UseFakeTime => true;

    private Task<HttpResponseMessage> WrongKey() => PostAsync("/Trailhead/dynu-key", new { Key = "not-the-right-key" });

    private Task<HttpResponseMessage> BadName(string route) => PostAsync(route, new { Label = "ab", Parent = "freeddns.org" });

    private static int Wait(HttpResponseMessage r) => (int)r.Headers.RetryAfter!.Delta!.Value.TotalSeconds;

    [Fact]
    public async Task The_eleventh_key_save_in_ten_minutes_is_429_with_Retry_After_and_does_not_reach_Dynu()
    {
        for (var i = 0; i < 10; i++)
        {
            using var ok = await WrongKey();
            Assert.Equal("dynu_key_rejected", Code(ok));
        }

        var before = DynuCalls;
        using var limited = await WrongKey();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", Code(limited));
        Assert.InRange(Wait(limited), 1, 600);
        Assert.Equal(before, DynuCalls);
        Assert.Equal(10, before);
    }

    [Fact]
    public async Task The_key_save_limit_lifts_when_the_window_has_passed()
    {
        for (var i = 0; i < 11; i++)
        {
            (await WrongKey()).Dispose();
        }

        Fake!.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        using var after = await WrongKey();

        Assert.Equal("dynu_key_rejected", Code(after));
    }

    [Fact]
    public async Task A_key_that_is_malformed_still_counts_against_the_limit()
    {
        for (var i = 0; i < 10; i++)
        {
            (await PostAsync("/Trailhead/dynu-key", "{not json")).Dispose();
        }

        using var limited = await PostAsync("/Trailhead/dynu-key", "{not json");

        Assert.Equal("rate_limited", Code(limited));
    }

    [Fact]
    public async Task The_sixty_first_name_check_in_a_minute_is_429()
    {
        for (var i = 0; i < 60; i++)
        {
            using var ok = await BadName("/Trailhead/name/check");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await BadName("/Trailhead/name/check");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", Code(limited));
        Assert.True(limited.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task The_eleventh_name_claim_in_a_minute_is_429()
    {
        for (var i = 0; i < 10; i++)
        {
            using var ok = await BadName("/Trailhead/name/claim");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await BadName("/Trailhead/name/claim");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", Code(limited));
        Assert.True(limited.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Each_route_has_its_own_limit()
    {
        for (var i = 0; i < 11; i++)
        {
            (await WrongKey()).Dispose();
        }

        using var check = await BadName("/Trailhead/name/check");
        using var claim = await BadName("/Trailhead/name/claim");

        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
    }

    [Fact]
    public async Task Callers_who_are_refused_for_having_no_token_do_not_use_up_the_limit()
    {
        for (var i = 0; i < 15; i++)
        {
            using var anon = await PostAsync("/Trailhead/dynu-key", new { Key = "x" }, role: null);
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        }

        using var admin = await WrongKey();

        Assert.Equal("dynu_key_rejected", Code(admin));
    }

    // ---- the body cap

    [Theory]
    [InlineData("/Trailhead/dynu-key")]
    [InlineData("/Trailhead/name/check")]
    [InlineData("/Trailhead/name/claim")]
    public async Task A_body_over_4_KB_is_refused_before_anything_is_read_into_the_request(string route)
    {
        var body = "{\"Key\":\"a\",\"Label\":\"a\",\"Parent\":\"b\",\"Pad\":\"" + new string('x', 5000) + "\"}";

        using var sized = await SendAsync(HttpMethod.Post, route, body: body);
        using var chunked = await SendAsync(HttpMethod.Post, route, body: body, tweak: r => r.Headers.TransferEncodingChunked = true);

        Assert.Equal(HttpStatusCode.BadRequest, sized.StatusCode);
        Assert.Equal("request_too_large", Code(sized));
        Assert.Equal(HttpStatusCode.BadRequest, chunked.StatusCode);
        Assert.Equal("request_too_large", Code(chunked));
        Assert.Equal(0, DynuCalls);
    }

    [Fact]
    public async Task A_body_just_under_the_cap_is_read()
    {
        var pad = new string('x', 4096 - "{\"Key\":\"short\",\"Pad\":\"\"}".Length);
        var body = "{\"Key\":\"short\",\"Pad\":\"" + pad + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(body) <= 4096);

        using var response = await SendAsync(HttpMethod.Post, "/Trailhead/dynu-key", body: body);

        Assert.Equal("dynu_key_rejected", Code(response));
    }

    [Fact]
    public async Task A_body_that_is_not_the_json_the_route_takes_is_a_400()
    {
        foreach (var bad in new[] { "", "not json", "[]", "null", "\"text\"", "{\"Key\":5}", "{\"Key\":" })
        {
            using var response = await SendAsync(HttpMethod.Post, "/Trailhead/dynu-key", body: bad);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("bad_request", Code(response));
        }

        Assert.Equal(0, DynuCalls);
    }
}

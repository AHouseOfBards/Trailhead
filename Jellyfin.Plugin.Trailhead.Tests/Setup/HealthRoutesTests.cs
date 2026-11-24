using System.Net;
using Jellyfin.Plugin.Trailhead.Edge;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>The health and phone-test routes through the real ASP.NET pipeline (auth is covered by ApiAuthTests).</summary>
[Collection(RegistryCollection.Name)]
public sealed class HealthRoutesTests : ApiTestBase
{
    [Fact]
    public async Task Before_any_run_the_report_is_null_and_stale()
    {
        using var response = await GetAsync("Trailhead/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonAsync(response);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("Report").ValueKind);
        Assert.True(doc.RootElement.GetProperty("Stale").GetBoolean());
    }

    [Fact]
    public async Task A_run_answers_a_report_and_the_next_get_has_it()
    {
        using (var run = await SendAsync(HttpMethod.Post, "Trailhead/health/run"))
        {
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        }

        using var response = await GetAsync("Trailhead/health");
        using var doc = await JsonAsync(response);
        var report = doc.RootElement.GetProperty("Report");
        Assert.Equal("not_set_up", report.GetProperty("Overall").GetString());
        Assert.Equal(6, report.GetProperty("Checks").GetArrayLength());
        Assert.False(doc.RootElement.GetProperty("Stale").GetBoolean());
    }

    [Fact]
    public async Task A_phone_test_link_needs_a_name_first()
    {
        using var response = await SendAsync(HttpMethod.Post, "Trailhead/phone-test");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_set_up", Code(response));
        Assert.Equal(PhoneTestStatus.None, Phone.State.Status);
    }

    [Fact]
    public async Task A_phone_test_link_is_on_the_name_over_https_and_its_state_is_waiting()
    {
        ConfigGate.Update(c => c.Hostname = "trailhead-test.example.com");

        using var response = await SendAsync(HttpMethod.Post, "Trailhead/phone-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        using var doc = await JsonAsync(response);
        var url = doc.RootElement.GetProperty("Url").GetString()!;
        Assert.Matches("^https://trailhead-test\\.example\\.com/\\.trailhead/phone/[0-9a-f]{32}$", url);

        using var state = await GetAsync("Trailhead/phone-test");
        using var stateDoc = await JsonAsync(state);
        Assert.Equal((int)PhoneTestStatus.Waiting, stateDoc.RootElement.GetProperty("Status").GetInt32());
    }

    [Fact]
    public async Task Each_new_link_has_a_new_token()
    {
        ConfigGate.Update(c => c.Hostname = "trailhead-test.example.com");

        using var a = await JsonAsync(await SendAsync(HttpMethod.Post, "Trailhead/phone-test"));
        using var b = await JsonAsync(await SendAsync(HttpMethod.Post, "Trailhead/phone-test"));

        Assert.NotEqual(a.RootElement.GetProperty("Url").GetString(), b.RootElement.GetProperty("Url").GetString());
    }
}

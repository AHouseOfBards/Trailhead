using System.Net;
using System.Reflection;
using Campground.Shared.Security;
using Jellyfin.Plugin.Trailhead.Api;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>
/// A controller with no authorization at all, in this test assembly only. It is here so the audit test can show that
/// the audit does find an open action; a check that has never failed proves nothing.
/// </summary>
[Route("OpenFixture")]
public sealed class OpenFixtureController : ControllerBase
{
    [HttpGet("open")]
    public IActionResult Open() => Ok();
}

/// <summary>
/// Auth through the real ASP.NET pipeline: Trailhead's controllers in a Kestrel host with the same scheme and the same
/// elevation policy as Jellyfin's, called without a token, as a user and as an admin.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class ApiAuthTests : ApiTestBase
{
    private static string Concrete(string path) => path.Replace("{lang}", "en", StringComparison.Ordinal);

    [Fact]
    public async Task Every_route_refuses_anonymous_callers_and_users_and_admits_admins()
    {
        var routes = Host.Routes();

        // A harness that finds nothing would pass every loop below.
        Assert.NotEmpty(routes);
        foreach (var (method, path) in routes)
        {
            var url = Concrete(path);
            var body = method == "POST" ? "{}" : null;

            using var anon = await SendAsync(new HttpMethod(method), url, role: null, body: body);
            using var user = await SendAsync(new HttpMethod(method), url, role: "user", body: body);
            using var admin = await SendAsync(new HttpMethod(method), url, role: "admin", body: body);

            Assert.True(anon.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} gave {(int)anon.StatusCode} with no token");
            Assert.True(user.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} gave {(int)user.StatusCode} for a user");
            Assert.True(admin.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{method} {path} gave {(int)admin.StatusCode} for an admin");
        }
    }

    [Fact]
    public void No_action_in_the_plugin_is_reachable_without_authorization()
    {
        Assert.Empty(AnonymousRouteAudit.Find(typeof(Plugin).Assembly));
    }

    [Fact]
    public void The_audit_does_find_an_action_that_has_no_authorization()
    {
        var open = AnonymousRouteAudit.Find(typeof(ApiAuthTests).Assembly);

        Assert.Contains(open, r => r.Controller == nameof(OpenFixtureController) && r.Action == nameof(OpenFixtureController.Open));
    }

    [Fact]
    public void The_real_routes_are_the_documented_routes_both_ways()
    {
        var real = Host.Routes().Select(r => r.Method + " " + r.Path).Order().ToList();
        var documented = DocumentedRoutes().Select(r => r.Method + " " + r.Path).Order().ToList();

        Assert.NotEmpty(real);
        Assert.NotEmpty(documented);
        Assert.Equal(documented, real);
        Assert.All(DocumentedRoutes(), r => Assert.Equal("admin", r.Level));
    }

    [Fact]
    public void The_documented_error_codes_are_the_codes_in_the_code_and_each_has_text()
    {
        var inCode = typeof(ApiCodes).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).Order().ToList();
        var documented = DocumentedCodes().Order().ToList();

        Assert.NotEmpty(inCode);
        Assert.NotEmpty(documented);
        Assert.Equal(inCode, documented);
        foreach (var code in inCode)
        {
            Assert.NotEqual("error." + code, UiText.Table.Get("error." + code, "en"));
        }
    }

    private static List<string> Section(string heading)
    {
        var lines = DocsText().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => l.Trim() == "## " + heading);
        if (start < 0)
        {
            return new List<string>();
        }

        return lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)).Where(l => l.StartsWith("| `", StringComparison.Ordinal)).ToList();
    }

    private static List<string> Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim().Trim('`')).ToList();

    private static List<(string Method, string Path, string Level)> DocumentedRoutes()
        => Section("All routes").Select(Cells).Select(c => (c[0], c[1].ToLowerInvariant(), c[2])).ToList();

    private static List<string> DocumentedCodes() => Section("Errors").Select(Cells).Select(c => c[0]).ToList();
}

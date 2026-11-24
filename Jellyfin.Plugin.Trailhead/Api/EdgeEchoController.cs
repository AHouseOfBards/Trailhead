using System.Text.RegularExpressions;
using Campground.Shared.Contracts;
using Jellyfin.Plugin.Trailhead.Edge;
using MediaBrowser.Common.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Trailhead.Api;

/// <summary>
/// The second half of Edge's self-test (the gate, T29): tells Edge which client address Jellyfin worked out for a
/// request Edge forwarded. It is the cheapest way to see that address from outside Jellyfin's pipeline: it runs inside
/// it, after <c>UseForwardedHeaders</c> (Jellyfin.Server/Startup.cs:184, v12.4), and reads the same
/// <c>GetNormalizedRemoteIP</c> that sessions and the dashboard use (MediaBrowser.Common/Extensions/HttpContextExtensions.cs:28).
/// </summary>
/// <remarks>
/// Anonymous, because the self-test has no Jellyfin login and must not need one. What keeps it closed: it answers 404,
/// the same as a missing route, unless the request carries this start's marker and reached Jellyfin from the
/// forwarding address (shared <see cref="HttpsClient.ThroughUnbufferedEdge"/>); only Edge knows the marker, and Edge
/// forwards this path only for its own self-test (a token only it has, from loopback). The answer holds nothing but
/// the caller's own address and the nonce it sent. Tried first: <c>/Sessions</c> (needs a login), and a startup
/// filter (runs ahead of <c>UseForwardedHeaders</c>, so it sees Edge's address, Bonfire R-1029).
/// </remarks>
[ApiController]
[Route("Trailhead/Edge")]
[AllowAnonymous]
public class EdgeEchoController : ControllerBase
{
    private static readonly Regex NonceShape = new("^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly EdgeIdentity _identity;

    public EdgeEchoController(EdgeIdentity identity) => _identity = identity;

    /// <summary>The nonce and the client address Jellyfin computed, as plain text; 404 for anything not from Edge's self-test.</summary>
    [HttpGet("Echo")]
    public IActionResult Echo([FromQuery] string? n)
    {
        try
        {
            if (n is null || !NonceShape.IsMatch(n) || !HttpsClient.ThroughUnbufferedEdge(_identity.AsRunningStatus(), HttpContext))
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-store";
            return Content(n + " " + HttpContext.GetNormalizedRemoteIP(), "text/plain");
        }
        catch (Exception)
        {
            return NotFound();
        }
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.Trailhead.Edge;
using Jellyfin.Plugin.Trailhead.Health;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Api;

/// <summary>
/// The health report and the phone test, for the status page. Admin only, on the class, like the setup routes. The
/// phone test's own answer is on Edge's listener (<c>/.trailhead/phone/{token}</c>), not here: Trailhead adds no
/// anonymous Jellyfin route for it.
/// </summary>
[ApiController]
[Route("Trailhead")]
[Authorize(Policy = Policies.RequiresElevation)]
public class HealthController : ControllerBase
{
    private readonly HealthServices _health;
    private readonly PhoneTest _phone;
    private readonly ILogger<HealthController> _logger;

    public HealthController(HealthServices health, ILogger<HealthController> logger)
    {
        _health = health;
        _phone = health.Phone;
        _logger = logger;
    }

    /// <summary>The last health report, and whether it is over an hour old.</summary>
    [HttpGet("health")]
    public Task<IActionResult> GetHealth()
        => Guard(_ =>
        {
            var service = _health.Health;
            var report = service.Last;
            return Task.FromResult<IActionResult>(Ok(new { Report = report, Stale = service.IsStale(report) }));
        });

    /// <summary>Runs the health check now (or waits for the one already running) and answers its report.</summary>
    [HttpPost("health/run")]
    public Task<IActionResult> RunHealth()
        => Guard(async ct =>
        {
            var service = _health.Health;
            var report = await service.RunAsync(ct).ConfigureAwait(false);
            return Ok(new { Report = report, Stale = false });
        });

    /// <summary>Makes a new phone-test link (the one before stops working).</summary>
    [HttpPost("phone-test")]
    public Task<IActionResult> CreatePhoneTest()
        => Guard(_ =>
        {
            var service = _health.Health;
            var host = service.Hostname();
            if (host.Length == 0)
            {
                return Task.FromResult(Fail(409, ApiCodes.NotSetUp));
            }

            var port = service.ExternalPort();
            var token = _phone.Create();
            var url = "https://" + host + (port == 443 ? string.Empty : ":" + port.ToString(CultureInfo.InvariantCulture)) + PhoneTest.PhonePrefix + token.Token;
            Response.Headers.CacheControl = "no-store";
            return Task.FromResult<IActionResult>(Ok(new { Url = url, token.ExpiresAt }));
        });

    /// <summary>Where the phone test is.</summary>
    [HttpGet("phone-test")]
    public Task<IActionResult> GetPhoneTest()
        => Guard(_ => Task.FromResult<IActionResult>(Ok(_phone.State)));

    private IActionResult Fail(int status, string code)
        => ErrorReply.Create(Response, UiText.Table, Request.Headers.AcceptLanguage, status, code);

    private async Task<IActionResult> Guard(Func<CancellationToken, Task<IActionResult>> work, [CallerMemberName] string action = "")
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            return await work(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception e)
        {
            // The type only: an exception's text can carry anything.
            _logger.LogWarning("Trailhead's {Action} route failed unexpectedly ({ErrorType}).", action, e.GetType().Name);
            return Fail(500, ApiCodes.Internal);
        }
    }
}

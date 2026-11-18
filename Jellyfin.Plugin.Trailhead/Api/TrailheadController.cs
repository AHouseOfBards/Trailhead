using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Trailhead.Dns;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Api;

/// <summary>
/// Trailhead's setup routes. Every one is admin only: the authorization sits on the class, so a route added later is
/// closed until someone opens it on purpose. No exception leaves an action: Jellyfin logs an escaped one with its
/// message and URL, and a Dynu error's text could carry the key.
/// </summary>
[ApiController]
[Route("Trailhead")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TrailheadController : ControllerBase
{
    // A key is at most 256 characters and a name two short words, so 4 KB is generous.
    private const int MaxBodyBytes = 4096;

    private static readonly Regex LanguageShape = new(@"^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string EtagBase = (typeof(TrailheadController).Assembly.GetName().Version ?? new Version(0, 0)).ToString();

    private readonly SetupService _setup;
    private readonly ILogger<TrailheadController> _logger;

    public TrailheadController(SetupService setup, ILogger<TrailheadController> logger)
    {
        _setup = setup;
        _logger = logger;
    }

    /// <summary>What the setup page needs to draw itself.</summary>
    [HttpGet("setup")]
    public Task<IActionResult> GetSetup()
        => Guard(_ => Task.FromResult<IActionResult>(Ok(_setup.GetState())));

    /// <summary>Checks a Dynu API key and, if Dynu accepts it, saves it sealed.</summary>
    [HttpPost("dynu-key")]
    [RequestSizeLimit(MaxBodyBytes)]
    public Task<IActionResult> SaveDynuKey()
        => Guard(async ct =>
        {
            if (!_setup.TryBegin(RateKind.KeySave, out var wait))
            {
                return Fail(429, ApiCodes.RateLimited, wait);
            }

            var (body, bad) = await ReadBodyAsync<KeyRequest>(ct).ConfigureAwait(false);
            if (body is null)
            {
                return bad!;
            }

            var result = await _setup.SaveKeyAsync(body.Key, ct).ConfigureAwait(false);
            return result.Failure switch
            {
                SetupFailure.None => Ok(new { result.NameCleared, result.Setup }),
                SetupFailure.KeyInvalid => Fail(400, ApiCodes.KeyInvalid),
                SetupFailure.KeyRejected => Fail(400, ApiCodes.KeyRejected),
                SetupFailure.Unavailable => Fail(503, ApiCodes.DynuUnavailable, 60),
                SetupFailure.SaveFailed => Fail(500, ApiCodes.SaveFailed),
                _ => Fail(500, ApiCodes.Internal),
            };
        });

    /// <summary>Looks a name up on the Dynu account. Saves nothing.</summary>
    [HttpPost("name/check")]
    [RequestSizeLimit(MaxBodyBytes)]
    public Task<IActionResult> CheckName()
        => Guard(async ct =>
        {
            if (!_setup.TryBegin(RateKind.NameCheck, out var wait))
            {
                return Fail(429, ApiCodes.RateLimited, wait);
            }

            var (body, bad) = await ReadBodyAsync<NameRequest>(ct).ConfigureAwait(false);
            if (body is null)
            {
                return bad!;
            }

            var result = await _setup.CheckNameAsync(body.Label, body.Parent, ct).ConfigureAwait(false);
            return NameAnswer(result, claiming: false);
        });

    /// <summary>Creates the name on the Dynu account (or finds it) and saves it as the address.</summary>
    [HttpPost("name/claim")]
    [RequestSizeLimit(MaxBodyBytes)]
    public Task<IActionResult> ClaimName()
        => Guard(async ct =>
        {
            if (!_setup.TryBegin(RateKind.NameClaim, out var wait))
            {
                return Fail(429, ApiCodes.RateLimited, wait);
            }

            var (body, bad) = await ReadBodyAsync<NameRequest>(ct).ConfigureAwait(false);
            if (body is null)
            {
                return bad!;
            }

            var result = await _setup.ClaimNameAsync(body.Label, body.Parent, ct).ConfigureAwait(false);
            return NameAnswer(result, claiming: true);
        });

    /// <summary>The page's strings in one language, with the fallbacks merged in.</summary>
    [HttpGet("strings/{lang}")]
    public Task<IActionResult> GetStrings(string lang)
        => Guard(_ =>
        {
            var wanted = (lang ?? string.Empty).ToLowerInvariant();
            if (wanted.Length > 35 || !LanguageShape.IsMatch(wanted))
            {
                return Task.FromResult(Fail(400, ApiCodes.BadRequest));
            }

            // Only a language that exists reaches the lookup, so a caller can't make the server build tables for
            // names it invents.
            var strings = _setup.Strings;
            var language = UiText.Resolve(strings, wanted);
            var etag = "\"" + EtagBase + "-" + language + "\"";
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "private, no-cache";
            Response.Headers.ContentLanguage = language;
            if (Request.Headers.IfNoneMatch.Any(v => v is not null && (v == "*" || v.Split(',').Any(t => t.Trim().Trim('W', '/') == etag))))
            {
                return Task.FromResult<IActionResult>(StatusCode(304));
            }

            return Task.FromResult<IActionResult>(new ContentResult
            {
                Content = JsonSerializer.Serialize(strings.TableFor(language)),
                ContentType = "application/json; charset=utf-8",
            });
        });

    private IActionResult NameAnswer(NameOutcome result, bool claiming)
    {
        switch (result.Failure)
        {
            case SetupFailure.None:
                var claim = result.Claim!;
                var language = UiText.FromAcceptLanguage(_setup.Strings, Request.Headers.AcceptLanguage);
                var message = NameClaimer.Describe(_setup.Strings, claim.Kind, claim.Problem, claim.Reason, language);
                var problem = claim.Problem == LabelProblem.None ? null : claim.Problem.ToString();
                return claiming
                    ? Ok(new { Kind = claim.Kind.ToString(), claim.Name, Problem = problem, Message = message, Alternatives = result.Alternatives ?? Array.Empty<string>(), result.Setup })
                    : Ok(new { Kind = claim.Kind.ToString(), claim.Name, Problem = problem, Message = message });
            case SetupFailure.BadKey:
                return Fail(409, ApiCodes.KeyRejected);
            case SetupFailure.KeyMissing:
                return Fail(409, ApiCodes.KeyMissing);
            case SetupFailure.Unavailable:
                return Fail(503, ApiCodes.DynuUnavailable, 60);
            case SetupFailure.SaveFailed:
                return Fail(500, ApiCodes.SaveFailed);
            default:
                return Fail(500, ApiCodes.Internal);
        }
    }

    private IActionResult Fail(int status, string code, int? retryAfter = null)
        => ErrorReply.Create(Response, _setup.Strings, Request.Headers.AcceptLanguage, status, code, retryAfter);

    // Reads the body here and not through model binding: a malformed or oversized body then lands in our own 400,
    // and nothing from the body (which can hold the key) is echoed back by a framework error.
    private async Task<(T? Body, IActionResult? Error)> ReadBodyAsync<T>(CancellationToken ct)
        where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(Request.Body, ReadOptions, ct).ConfigureAwait(false);
            return body is null ? (null, Fail(400, ApiCodes.BadRequest)) : (body, null);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Fail(400, ApiCodes.RequestTooLarge));
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException or InvalidOperationException or NotSupportedException)
        {
            return (null, Fail(400, ApiCodes.BadRequest));
        }
    }

    private async Task<IActionResult> Guard(Func<CancellationToken, Task<IActionResult>> work, [CallerMemberName] string action = "")
    {
        var ct = HttpContext.RequestAborted;
        try
        {
            return await work(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller went away; nobody is left to read an answer.
            return new EmptyResult();
        }
        catch (Exception e)
        {
            // The type only: an exception's text can carry anything, a key included.
            _logger.LogWarning("Trailhead's {Action} route failed unexpectedly ({ErrorType}).", action, e.GetType().Name);
            return Fail(500, ApiCodes.Internal);
        }
    }

    private sealed record KeyRequest(string? Key);

    private sealed record NameRequest(string? Label, string? Parent);
}

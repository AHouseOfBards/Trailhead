using System.Globalization;
using Campground.Shared.I18n;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Trailhead.Api;

/// <summary>
/// Every error code Trailhead's routes send. Each is snake_case, never changes once released, and is listed in
/// docs/api.md. A caller branches on these, not on the words.
/// </summary>
internal static class ApiCodes
{
    internal const string KeyInvalid = "dynu_key_invalid";
    internal const string KeyRejected = "dynu_key_rejected";
    internal const string KeyMissing = "dynu_key_missing";
    internal const string DynuUnavailable = "dynu_unavailable";
    internal const string RateLimited = "rate_limited";
    internal const string BadRequest = "bad_request";
    internal const string RequestTooLarge = "request_too_large";
    internal const string SaveFailed = "save_failed";
    internal const string Internal = "internal";
}

/// <summary>
/// The error answer of api.md A3: the status, a plain-text translated body, <c>X-Campground-Error</c>, and
/// <c>Retry-After</c> for 429 and 503. Campground.Shared has no such helper yet, so this is local; it swaps for the
/// shared one when that is vendored.
/// </summary>
internal static class ErrorReply
{
    internal const string CodeHeader = "X-Campground-Error";

    internal static IActionResult Create(HttpResponse response, Strings strings, string? acceptLanguage, int status, string code, int? retryAfterSeconds = null)
    {
        var language = UiText.FromAcceptLanguage(strings, acceptLanguage);
        response.Headers[CodeHeader] = code;
        response.Headers.Vary = "Accept-Language";
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return new ContentResult
        {
            StatusCode = status,
            Content = strings.Get("error." + code, language),
            ContentType = "text/plain; charset=utf-8",
        };
    }
}

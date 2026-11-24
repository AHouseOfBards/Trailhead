using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>Where the phone test is. Codes the page reads.</summary>
internal enum PhoneTestStatus
{
    /// <summary>No token has been made since Jellyfin started.</summary>
    None,

    /// <summary>A token is out and nothing has used it yet.</summary>
    Waiting,

    /// <summary>The token came back, but from this server's own network: not proof (turn Wi-Fi off and try again).</summary>
    FromHome,

    /// <summary>The token came back from outside the home network. Proof that the path works.</summary>
    Passed,

    /// <summary>Fifteen minutes went by without a hit from outside.</summary>
    Expired,
}

/// <summary>The phone test as the page sees it. No address, only its family.</summary>
internal sealed record PhoneTestState(PhoneTestStatus Status, DateTimeOffset? ExpiresAt, DateTimeOffset? HitAt, string? Family);

/// <summary>A new token, for the admin route to hand to the page.</summary>
internal sealed record PhoneTestToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The phone test (T9, A7): a one-time token the admin opens on a phone with Wi-Fi off. Edge answers it at
/// <c>/.trailhead/phone/{token}</c> on its own listener, not through a Jellyfin route, so Trailhead adds no anonymous
/// Jellyfin route for it. Only our own random token coming back counts: a connection alone could be an ISP filter or
/// the router's own page. In memory only, one token at a time, 15 minutes each.
/// </summary>
internal sealed class PhoneTest
{
    internal const string Prefix = "/.trailhead/";
    internal const string PhonePrefix = "/.trailhead/phone/";
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private byte[]? _token;
    private DateTimeOffset _expires;
    private PhoneTestState _state = new(PhoneTestStatus.None, null, null, null);

    internal PhoneTest(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Makes a new token (128 random bits) and drops the one before.</summary>
    internal PhoneTestToken Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var text = Convert.ToHexString(bytes).ToLowerInvariant();
        lock (_gate)
        {
            _token = Encoding.ASCII.GetBytes(text);
            _expires = _time.GetUtcNow() + Lifetime;
            _state = new PhoneTestState(PhoneTestStatus.Waiting, _expires, null, null);
        }

        return new PhoneTestToken(text, _expires);
    }

    internal PhoneTestState State
    {
        get
        {
            lock (_gate)
            {
                if (_state.Status is PhoneTestStatus.Waiting or PhoneTestStatus.FromHome && _time.GetUtcNow() >= _expires)
                {
                    _token = null;
                    _state = _state with { Status = PhoneTestStatus.Expired };
                }

                return _state;
            }
        }
    }

    /// <summary>
    /// A request for <c>/.trailhead/phone/{token}</c>. True when the token is the live one. A hit from outside uses it
    /// up; a hit from home is recorded and leaves it live, so the same code still works once the phone's Wi-Fi is off.
    /// </summary>
    internal bool Hit(string token, IPAddress remote, bool fromHome)
    {
        lock (_gate)
        {
            if (_token is null || _time.GetUtcNow() >= _expires || token.Length != _token.Length
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token), _token))
            {
                return false;
            }

            var family = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
            if (fromHome)
            {
                _state = new PhoneTestState(PhoneTestStatus.FromHome, _expires, _time.GetUtcNow(), family);
                return true;
            }

            _token = null;
            _state = new PhoneTestState(PhoneTestStatus.Passed, _expires, _time.GetUtcNow(), family);
            return true;
        }
    }

    /// <summary>
    /// Answers anything under <c>/.trailhead/</c> on Edge. Edge calls it only once its gate has passed: before that the
    /// path to Jellyfin isn't proven, so a pass here would prove nothing about remote access.
    /// </summary>
    internal async Task HandleAsync(HttpContext ctx, IPAddress remote, bool fromHome)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var get = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method);
        var token = path.StartsWith(PhonePrefix, StringComparison.Ordinal) ? path[PhonePrefix.Length..] : null;
        ctx.Response.Headers.CacheControl = "no-store";
        if (!get || token is null || token.Length != 32 || !token.All(char.IsAsciiHexDigitLower) || !Hit(token, remote, fromHome))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
        var text = WebUtility.HtmlEncode(UiText.English(fromHome ? "phone.page.home" : "phone.page.works"));
        var title = WebUtility.HtmlEncode(UiText.English("phone.page.title"));
        var html = string.Format(
            CultureInfo.InvariantCulture,
            "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{0}</title><style>body{{font-family:sans-serif;margin:2em;font-size:1.3em}}</style></head><body><p>{1}</p></body></html>",
            title,
            text);
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.Response.WriteAsync(html, ctx.RequestAborted).ConfigureAwait(false);
        }
    }
}

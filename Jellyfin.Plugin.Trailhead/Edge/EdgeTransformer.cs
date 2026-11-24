using Campground.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>
/// The header rules (03 §2, Edge row): the forwarding headers are Edge's own, never the client's; a client's marker is
/// dropped and this start's marker stamped; hop-by-hop headers go. Every other header, the query string and the body
/// go through as they came.
/// </summary>
internal sealed class EdgeTransformer : HttpTransformer
{
    /// <summary>The self-test's header. Only Edge's own self-test sends it, and it never goes on to Jellyfin.</summary>
    internal const string ProbeHeader = "X-Trailhead-Probe";

    /// <summary>Where the pipeline leaves this request's names from <see cref="ConnectionNames"/>.</summary>
    internal const string RequestItemKey = "trailhead.request-connection-names";

    // What a client could use to pose as a proxy, or as Edge. X-Original-* too: Jellyfin's forwarded-headers middleware
    // writes them when it applies the X-Forwarded-* ones, and shared ThroughUnbufferedEdge reads X-Original-For.
    private static readonly string[] ClientMayNotSend =
    {
        "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Port", "X-Forwarded-Prefix",
        "Forwarded", "X-Real-IP", "X-Original-For", "X-Original-Proto", "X-Original-Host", "X-Original-Prefix",
        HttpsClient.EdgeMarkerHeader, ProbeHeader,
    };

    // RFC 9110 §7.6.1 names Connection, Proxy-Connection, Keep-Alive, TE, Transfer-Encoding and Upgrade. YARP already
    // leaves out Connection, Keep-Alive, Transfer-Encoding and Proxy-Connection, and handles TE and Upgrade itself
    // (TE: trailers for gRPC, Upgrade for WebSockets), so those two stay with it. The list is repeated here for the
    // ones that are never wanted, so a YARP change can't let them through.
    private static readonly string[] HopByHop = { "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding" };

    // Connection options that are about this connection only and name no header.
    private static readonly string[] ConnectionOptions = { "keep-alive", "close", "upgrade" };

    private readonly Func<string> _marker;

    internal EdgeTransformer(Func<string> marker) => _marker = marker;

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);

        foreach (var name in NamedInConnection(httpContext))
        {
            proxyRequest.Headers.Remove(name);
            proxyRequest.Content?.Headers.Remove(name);
        }

        foreach (var name in HopByHop.Concat(ClientMayNotSend))
        {
            proxyRequest.Headers.Remove(name);
        }

        var remote = httpContext.Connection.RemoteIpAddress;
        if (remote is not null)
        {
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString());
        }

        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        if (httpContext.Request.Host.HasValue)
        {
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", httpContext.Request.Host.Value);
        }

        proxyRequest.Headers.TryAddWithoutValidation(HttpsClient.EdgeMarkerHeader, _marker());
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        var go = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);

        // Jellyfin doesn't send the marker back. If anything ever did, it would end at the client.
        httpContext.Response.Headers.Remove(HttpsClient.EdgeMarkerHeader);
        return go;
    }

    /// <summary>
    /// The header names a request's Connection header nominates as hop-by-hop (RFC 9110 §7.6.1): what Kestrel kept of
    /// the header, and what <see cref="ConnectionNames"/> read off the wire, since Kestrel keeps only its own options.
    /// </summary>
    internal static IEnumerable<string> NamedInConnection(HttpContext httpContext)
    {
        var fromWire = httpContext.Items.TryGetValue(RequestItemKey, out var v) && v is string[] names ? names : Array.Empty<string>();
        foreach (var value in httpContext.Request.Headers.Connection.Concat(fromWire))
        {
            foreach (var token in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!ConnectionOptions.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    yield return token;
                }
            }
        }
    }
}

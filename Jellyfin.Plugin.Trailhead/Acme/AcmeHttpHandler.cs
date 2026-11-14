using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>
/// Sits in front of the HttpClient that Certes is given. It puts a time limit on every request, keeps the
/// Retry-After of error responses, and logs the method, the host and the status of each call and nothing else:
/// ACME URLs carry account and order ids, and challenge errors can carry tokens.
/// </summary>
internal sealed class AcmeHttpHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private TimeSpan? _lastRetryAfter;

    internal AcmeHttpHandler(HttpMessageHandler inner, ILogger logger, TimeProvider time, TimeSpan timeout)
        : base(inner)
    {
        _logger = logger;
        _time = time;
        _timeout = timeout;
    }

    /// <summary>
    /// The Retry-After of the most recent error response, cleared by the next success. Certes keeps it for
    /// success responses only (AcmeError has no field for it), and a rateLimited answer is exactly when it matters.
    /// </summary>
    internal TimeSpan? LastRetryAfter
    {
        get
        {
            lock (_gate)
            {
                return _lastRetryAfter;
            }
        }
    }

    /// <summary>
    /// An HttpClient for Certes. With <paramref name="extraCa"/> it also trusts that one CA, for this client only
    /// and never through the machine's store (tests use it for Pebble's CA). Without it, the system's trust applies.
    /// </summary>
    internal static (HttpClient Client, AcmeHttpHandler Handler) Create(ILogger logger, TimeProvider time, TimeSpan timeout, X509Certificate2? extraCa = null)
    {
        var sockets = new SocketsHttpHandler();
        if (extraCa is not null)
        {
            sockets.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) => ValidateAgainst(extraCa, cert, errors),
            };
        }

        var handler = new AcmeHttpHandler(sockets, logger, time, timeout);

        // The handler enforces the timeout; HttpClient's own would report a plain cancel that can't be told apart
        // from the caller giving up.
        return (new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan }, handler);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);

            // Read the body here, under the same limit: HttpClient would otherwise read it after we return, with
            // no limit, and a CA that stalls mid-body would hang the run.
            await response.Content.LoadIntoBufferAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // Without this the caller can't tell "we gave up on a slow CA" from "the user cancelled".
            throw new TimeoutException($"{request.Method} {request.RequestUri?.Host} took longer than {_timeout}.");
        }

        lock (_gate)
        {
            _lastRetryAfter = response.IsSuccessStatusCode ? null : ParseRetryAfter(response.Headers.RetryAfter, _time.GetUtcNow());
        }

        _logger.LogInformation("ACME {Method} {Host} -> {Status}", request.Method, request.RequestUri?.Host, (int)response.StatusCode);
        return response;
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static bool ValidateAgainst(X509Certificate2 ca, X509Certificate? cert, SslPolicyErrors errors)
    {
        // A name mismatch must still fail; only chain errors are re-judged, against our CA alone.
        if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
    }
}

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>What the certificate check found. Codes the page and the alerts read.</summary>
internal static class CertificateCodes
{
    internal const string Ok = "ok";
    internal const string ExpiresSoon = "expires_soon";
    internal const string Expired = "expired";
    internal const string WrongName = "wrong_name";
    internal const string NotStored = "not_the_stored_one";
    internal const string ListenerDown = "listener_down";
    internal const string NoHandshake = "no_handshake";
}

/// <summary>The certificate a client of Edge is given right now.</summary>
internal sealed record ServedCertificateFacts(string Code, DateTimeOffset? NotAfter, string? Thumbprint);

/// <summary>
/// Reads the certificate Edge is <b>serving</b> (A1), with a TLS handshake over loopback and validation off, rather
/// than the one in the store: one check then proves the listener runs on the port the table says, and that a renewal
/// has really reached it. RASA checked the file and missed a listener serving an old or wrong certificate.
/// </summary>
internal static class ServedCertificate
{
    /// <summary>The alert starts this long before expiry (03 §3b). Renewal at 30 days left is normal, so 30 would cry wolf.</summary>
    internal static readonly TimeSpan AlertBefore = TimeSpan.FromDays(14);

    internal static async Task<ServedCertificateFacts> CheckAsync(IPEndPoint? edge, string hostname, string? storedThumbprint, DateTimeOffset now, TimeSpan timeout, CancellationToken ct)
    {
        if (edge is null)
        {
            return new ServedCertificateFacts(CertificateCodes.ListenerDown, null, null);
        }

        X509Certificate2? served;
        try
        {
            served = await HandshakeAsync(edge, hostname, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is SocketException or IOException or AuthenticationException or OperationCanceledException or TimeoutException)
        {
            // Refused or reset: nothing is listening there (the table and the listener disagree, or Edge died).
            return new ServedCertificateFacts(e is SocketException ? CertificateCodes.ListenerDown : CertificateCodes.NoHandshake, null, null);
        }

        if (served is null)
        {
            return new ServedCertificateFacts(CertificateCodes.NoHandshake, null, null);
        }

        using (served)
        {
            var notAfter = new DateTimeOffset(served.NotAfter.ToUniversalTime(), TimeSpan.Zero);
            var thumb = served.Thumbprint;
            string code;
            if (!served.MatchesHostname(hostname, allowWildcards: true, allowCommonName: false))
            {
                code = CertificateCodes.WrongName;
            }
            else if (notAfter <= now)
            {
                code = CertificateCodes.Expired;
            }
            else if (storedThumbprint is null || !thumb.Equals(storedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                // Serving something the store doesn't hold: a renewal that never reached the listener.
                code = CertificateCodes.NotStored;
            }
            else if (notAfter - now <= AlertBefore)
            {
                code = CertificateCodes.ExpiresSoon;
            }
            else
            {
                code = CertificateCodes.Ok;
            }

            return new ServedCertificateFacts(code, notAfter, thumb);
        }
    }

    private static async Task<X509Certificate2?> HandshakeAsync(IPEndPoint edge, string hostname, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        using var tcp = new Socket(edge.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await tcp.ConnectAsync(edge, limit.Token).ConfigureAwait(false);
        await using var stream = new NetworkStream(tcp, ownsSocket: false);
        X509Certificate2? seen = null;
        await using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = hostname,

                // Validation off on purpose: the point is to see what is served, even when it is wrong. Nothing is
                // sent over this connection.
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is not null)
                    {
                        seen = new X509Certificate2(cert);
                    }

                    return true;
                },
            },
            limit.Token).ConfigureAwait(false);
        return seen;
    }
}

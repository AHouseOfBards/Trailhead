using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>The CA's suggested renewal window (ARI, RFC 9773).</summary>
internal sealed record RenewalWindow(DateTimeOffset Start, DateTimeOffset End);

internal abstract record RenewalDecision
{
    private RenewalDecision()
    {
    }

    internal sealed record RenewNow : RenewalDecision;

    internal sealed record CheckAt(DateTimeOffset Time) : RenewalDecision;
}

/// <summary>When to renew. Pure: the clock and the random value come in, so tests need neither.</summary>
internal static class RenewalPolicy
{
    /// <summary>
    /// Renew with a third of the lifetime left, or earlier at a point inside the CA's ARI window. The window never
    /// delays renewal past the one-third point: a CA that answers with a late window must not leave us with a
    /// certificate close to expiry.
    /// </summary>
    /// <param name="random">
    /// A value in [0, 1) that picks the point inside the window. It must be the same for the same certificate on
    /// every call, or the point would move each time we looked and renewal would keep sliding; use
    /// <see cref="FractionFor"/>.
    /// </param>
    internal static RenewalDecision Decide(DateTimeOffset notBefore, DateTimeOffset notAfter, RenewalWindow? ari, DateTimeOffset now, double random)
    {
        var target = notAfter - ((notAfter - notBefore) / 3);

        if (ari is not null)
        {
            var span = ari.End > ari.Start ? ari.End - ari.Start : TimeSpan.Zero;
            var point = ari.Start + (span * Math.Clamp(random, 0.0, 1.0));
            if (point < target)
            {
                target = point;
            }
        }

        return now >= target ? new RenewalDecision.RenewNow() : new RenewalDecision.CheckAt(target);
    }

    /// <summary>A stable pseudo-random value in [0, 1) for a certificate, from its thumbprint.</summary>
    internal static double FractionFor(string thumbprint)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(thumbprint));
        return (BitConverter.ToUInt64(hash, 0) >> 11) / (double)(1UL << 53);
    }
}

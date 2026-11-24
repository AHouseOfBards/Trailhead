using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.Trailhead.Acme;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>A certificate ready for TLS: the leaf with its key and the chain to send with it.</summary>
internal sealed class ServingCertificate
{
    private ServingCertificate(X509Certificate2 leaf, SslStreamCertificateContext context)
    {
        Leaf = leaf;
        Context = context;
    }

    internal X509Certificate2 Leaf { get; }

    internal SslStreamCertificateContext Context { get; }

    internal string Thumbprint => Leaf.Thumbprint;

    /// <summary>
    /// Builds the TLS context with the issuers given, offline. Kestrel's <c>ServerCertificateSelector</c> (the spike's
    /// way) hands back a bare certificate, with no place for the issuers, so the chain sent would be whatever the
    /// machine's store can find; an intermediate missing there fails old Android TVs (T34). The handshake callback
    /// with this context sends the leaf and the intermediate as the CA served them (EdgeTests checks the client gets both).
    /// </summary>
    internal static ServingCertificate From(StoredCertificate stored)
    {
        var issuers = new X509Certificate2Collection();
        foreach (var c in stored.Chain.Skip(1))
        {
            issuers.Add(c);
        }

        return new ServingCertificate(stored.Leaf, SslStreamCertificateContext.Create(stored.Leaf, issuers, offline: true));
    }
}

/// <summary>
/// The certificate Edge serves, swapped atomically (03 §3b, swap safely). A connection takes the reference in its
/// handshake, so an open connection keeps the one it started with and a new connection gets the new one. The one
/// before is kept, not disposed, until the new one has served a real handshake: disposing a certificate an open
/// TLS stream may still use isn't worth the risk, and once nothing refers to it the runtime cleans it up.
/// </summary>
internal sealed class CertificateSlot
{
    private ServingCertificate? _current;
    private ServingCertificate? _previous;

    internal ServingCertificate? Current => Volatile.Read(ref _current);

    /// <summary>The one before, until the current one has served a handshake (tests).</summary>
    internal ServingCertificate? Previous => Volatile.Read(ref _previous);

    /// <summary>Puts a new certificate in place. False when it is the one already served.</summary>
    internal bool Swap(ServingCertificate next)
    {
        var old = Interlocked.Exchange(ref _current, next);
        if (old is not null && old.Thumbprint == next.Thumbprint)
        {
            return false;
        }

        Volatile.Write(ref _previous, old);
        return true;
    }

    /// <summary>The certificate for the handshake starting now. Null when there is none.</summary>
    internal ServingCertificate? ForHandshake()
    {
        var current = Current;
        if (current is not null && Previous is not null)
        {
            Volatile.Write(ref _previous, null);
        }

        return current;
    }
}

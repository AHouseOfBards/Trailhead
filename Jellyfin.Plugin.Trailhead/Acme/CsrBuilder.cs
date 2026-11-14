using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>
/// An RSA-2048 key and its CSR, made with System.Security.Cryptography only, so BouncyCastle never sees the
/// certificate key. PKCS#1 v1.5 padding is explicit: Boulder (and Pebble) refuse an RSA-PSS CSR as badCSR.
/// </summary>
internal sealed class CsrBuilder : IDisposable
{
    internal CsrBuilder() => Key = RSA.Create(2048);

    internal RSA Key { get; }

    /// <summary>The CSR, DER encoded. The first name is the subject; every name is in the SAN.</summary>
    internal byte[] BuildDer(IReadOnlyList<string> names)
    {
        if (names is null || names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A CSR needs at least one name.", nameof(names));
        }

        var request = new CertificateRequest(new X500DistinguishedName("CN=" + names[0]), Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        request.CertificateExtensions.Add(san.Build());
        return request.CreateSigningRequest();
    }

    public void Dispose() => Key.Dispose();
}

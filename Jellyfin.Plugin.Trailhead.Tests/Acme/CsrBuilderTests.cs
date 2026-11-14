using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.Trailhead.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class CsrBuilderTests
{
    private const string Sha256WithRsa = "1.2.840.113549.1.1.11";

    private static string SignatureOid(byte[] der)
    {
        // CertificationRequest ::= SEQUENCE { certificationRequestInfo, signatureAlgorithm, signature }
        var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        outer.ReadEncodedValue();
        return outer.ReadSequence().ReadObjectIdentifier();
    }

    [Fact]
    public void The_signature_is_PKCS1_v1_5_not_PSS()
    {
        using var csr = new CsrBuilder();

        var oid = SignatureOid(csr.BuildDer(new[] { "home.example.org" }));

        // Boulder refuses RSA-PSS CSRs (badCSR), seen against Pebble in the T-S2 spike.
        Assert.Equal(Sha256WithRsa, oid);
    }

    [Fact]
    public void The_key_is_RSA_2048_and_is_the_key_in_the_CSR()
    {
        using var csr = new CsrBuilder();
        var der = csr.BuildDer(new[] { "home.example.org" });

        Assert.Equal(2048, csr.Key.KeySize);
        var loaded = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        Assert.Equal(csr.Key.ExportSubjectPublicKeyInfo(), loaded.PublicKey.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void The_SAN_lists_every_name_and_the_subject_is_the_first()
    {
        using var csr = new CsrBuilder();
        var der = csr.BuildDer(new[] { "home.example.org", "tv.example.org" });

        var loaded = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
        var san = Assert.Single(loaded.CertificateExtensions.OfType<X509SubjectAlternativeNameExtension>());

        Assert.Equal(new[] { "home.example.org", "tv.example.org" }, san.EnumerateDnsNames().OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("CN=home.example.org", loaded.SubjectName.Name);
    }

    [Fact]
    public void Every_builder_has_its_own_key()
    {
        using var a = new CsrBuilder();
        using var b = new CsrBuilder();

        Assert.NotEqual(a.Key.ExportSubjectPublicKeyInfo(), b.Key.ExportSubjectPublicKeyInfo());
    }

    [Fact]
    public void No_names_is_refused()
    {
        using var csr = new CsrBuilder();

        Assert.Throws<ArgumentException>(() => csr.BuildDer(Array.Empty<string>()));
    }
}

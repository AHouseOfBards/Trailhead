using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

internal sealed class TempFolder : IDisposable
{
    internal TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trailhead-acme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ListLogger : ILogger
{
    private readonly List<(LogLevel Level, string Text)> _entries = new();

    internal IReadOnlyList<(LogLevel Level, string Text)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    internal string All => string.Join("\n", Entries.Select(e => e.Text));

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception) + (exception == null ? string.Empty : " | " + exception);
        lock (_entries)
        {
            _entries.Add((logLevel, text));
        }
    }
}

/// <summary>A throwaway CA hierarchy: root, one intermediate, and leaves under it.</summary>
internal static class TestPki
{
    private static readonly DateTimeOffset CaFrom = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CaTo = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The chain PEM a CA would serve (leaf, then the intermediate) and the leaf's key.</summary>
    internal static (string ChainPem, RSA Key, X509Certificate2 Root) Issue(string name, string rootCn, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=" + rootCn, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(CaFrom, CaTo);

        using var interKey = RSA.Create(2048);
        var interReq = new CertificateRequest("CN=Test Intermediate R1", interKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        interReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        using var interPublic = interReq.Create(root, CaFrom, CaTo, RandomNumberGenerator.GetBytes(8));
        using var inter = interPublic.CopyWithPrivateKey(interKey);

        var leafKey = RSA.Create(2048);
        var leafReq = new CertificateRequest("CN=" + name, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        leafReq.CertificateExtensions.Add(san.Build());
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var leaf = leafReq.Create(inter, notBefore, notAfter, RandomNumberGenerator.GetBytes(8));

        var pem = leaf.ExportCertificatePem() + "\n" + inter.ExportCertificatePem() + "\n";
        return (pem, leafKey, X509CertificateLoader.LoadCertificate(root.RawData));
    }

    /// <summary>A server certificate for 127.0.0.1 under a fresh CA, loaded so Schannel can use it.</summary>
    internal static (X509Certificate2 Server, X509Certificate2 Ca) Server()
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test Server CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var leaf = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(8));
        using var withKey = leaf.CopyWithPrivateKey(key);
        var server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "x"), "x");
        return (server, X509CertificateLoader.LoadCertificate(ca.RawData));
    }
}

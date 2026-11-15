using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Campground.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>What a certificate must be before it replaces the current one.</summary>
/// <param name="Name">The hostname that must be in the certificate's SAN.</param>
/// <param name="RootCommonName">The common name of the root that issued the last certificate in the chain.</param>
internal sealed record CertificateExpectation(string Name, string RootCommonName);

internal sealed record StoreResult(bool Ok, string? Reason);

internal sealed class StoredCertificate
{
    internal StoredCertificate(X509Certificate2 leaf, IReadOnlyList<X509Certificate2> chain)
    {
        Leaf = leaf;
        Chain = chain;
    }

    /// <summary>The certificate with its private key.</summary>
    internal X509Certificate2 Leaf { get; }

    /// <summary>The leaf first, then the issuers up to the one the root signed.</summary>
    internal IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>The certificates only (no key), leaf first, as the CA served them.</summary>
    internal string ChainPem() => string.Concat(Chain.Select(c => c.ExportCertificatePem() + "\n"));
}

/// <summary>
/// The current certificate and the one before it, as PFX files in a private folder. A new certificate is staged
/// in a temp file and checked as it will be read back, and only then moved into place, so a bad one (or a crash
/// half way) never replaces a good one.
/// </summary>
internal sealed class CertificateStore
{
    private const string CurrentName = "current.pfx";
    private const string PreviousName = "previous.pfx";

    // The folder is private (PrivateFiles) and is the protection; Jellyfin's own certificate setting takes a
    // password, but nothing here would keep it anywhere safer than next to the file.
    private const string PfxPassword = "";

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    internal CertificateStore(string folder, TimeProvider time, ILogger logger)
    {
        _folder = folder;
        _time = time;
        _logger = logger;
        PrivateFiles.CreateFolder(folder);
    }

    internal StoredCertificate? Load() => Read(Path.Combine(_folder, CurrentName));

    internal StoredCertificate? LoadPrevious() => Read(Path.Combine(_folder, PreviousName));

    internal StoreResult Install(string chainPem, RSA key, CertificateExpectation expected)
    {
        lock (_gate)
        {
            var temp = Path.Combine(_folder, CurrentName + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                return InstallCore(chainPem, key, expected, temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                _logger.LogWarning("Trailhead could not store the new certificate ({Error}); the current one is unchanged.", e.GetType().Name);
                return new StoreResult(false, "The certificate could not be written: " + e.GetType().Name);
            }
            finally
            {
                PrivateWrite.TryDelete(temp);
            }
        }
    }

    private StoreResult InstallCore(string chainPem, RSA key, CertificateExpectation expected, string temp)
    {
        var certs = ParseChain(chainPem);
        if (certs.Count == 0)
        {
            return Refuse("The CA's answer holds no certificate.");
        }

        var collection = new X509Certificate2Collection();
        using (var leafWithKey = KeyMatches(certs[0], key) ? certs[0].CopyWithPrivateKey(key) : null)
        {
            if (leafWithKey is null)
            {
                return Refuse("The certificate's key does not match our private key.");
            }

            collection.Add(leafWithKey);
            foreach (var issuer in certs.Skip(1))
            {
                collection.Add(issuer);
            }

            PrivateFiles.WriteNew(temp, collection.Export(X509ContentType.Pfx, PfxPassword)!);
        }

        // Checked as read back from the file, which is the form the server will use.
        var staged = Read(temp);
        if (staged is null)
        {
            return Refuse("The staged certificate file could not be read back.");
        }

        var problem = Check(staged, key, expected);
        if (problem is not null)
        {
            return Refuse(problem);
        }

        var current = Path.Combine(_folder, CurrentName);
        if (File.Exists(current))
        {
            // Copy, not move: current.pfx must exist at every instant, so the replacement below is the only step
            // that changes it, and it is atomic.
            File.Copy(current, Path.Combine(_folder, PreviousName), overwrite: true);
        }

        File.Move(temp, current, overwrite: true);
        return new StoreResult(true, null);
    }

    private StoreResult Refuse(string reason)
    {
        _logger.LogWarning("Trailhead refused a new certificate: {Reason}", reason);
        return new StoreResult(false, reason);
    }

    private static bool KeyMatches(X509Certificate2 cert, RSA key)
    {
        using var publicKey = cert.GetRSAPublicKey();
        return publicKey is not null && publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo());
    }

    private string? Check(StoredCertificate staged, RSA key, CertificateExpectation expected)
    {
        var leaf = staged.Leaf;
        if (!leaf.HasPrivateKey || !KeyMatches(leaf, key))
        {
            return "The certificate's key does not match our private key.";
        }

        var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames());
        if (!names.Contains(expected.Name, StringComparer.OrdinalIgnoreCase))
        {
            return $"The certificate is not for the name {expected.Name}.";
        }

        var now = _time.GetUtcNow();
        if (leaf.NotAfter.ToUniversalTime() <= now.UtcDateTime)
        {
            return "The certificate has expired.";
        }

        if (leaf.NotAfter.ToUniversalTime() - now.UtcDateTime < TimeSpan.FromDays(1))
        {
            return "The certificate is valid for less than 1 day.";
        }

        var top = staged.Chain[^1];
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var other in staged.Chain.Skip(1))
        {
            chain.ChainPolicy.ExtraStore.Add(other);
        }

        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;

        // No downloads: with them the builder followed the top certificate's AIA caIssuers URL and returned 4
        // elements for the 3 the CA served (a live staging run), and the check must work offline anyway.
        chain.ChainPolicy.DisableCertificateDownloads = true;

        // The root isn't in what the CA serves, and a non-self-signed certificate can't be made the trust anchor
        // (that gave PartialChain). So the build is expected to end "untrusted"; what must hold is that it used
        // every certificate we were given, with no other problem: signatures, dates, CA flags.
        chain.Build(leaf);
        var chainProblems = chain.ChainStatus.Where(s => s.Status is not (X509ChainStatusFlags.PartialChain or X509ChainStatusFlags.UntrustedRoot)).ToList();
        if (chainProblems.Count > 0)
        {
            return "The certificate chain does not verify: " + string.Join(", ", chainProblems.Select(s => s.Status));
        }

        // The served certificates must come first, in order. The builder may add what the top one's issuer
        // resolved to (a root from the machine's store, say), so a count match is wrong; only that one may follow.
        var elements = chain.ChainElements;
        if (elements.Count < staged.Chain.Count)
        {
            return "The certificate chain does not link up.";
        }

        for (var i = 0; i < staged.Chain.Count; i++)
        {
            if (!elements[i].Certificate.RawData.AsSpan().SequenceEqual(staged.Chain[i].RawData))
            {
                return "The certificate chain does not link up.";
            }
        }

        if (elements.Count > staged.Chain.Count
            && !string.Equals(elements[staged.Chain.Count].Certificate.Subject, top.Issuer, StringComparison.Ordinal))
        {
            return "The certificate chain does not link up.";
        }

        // The last certificate in the chain is issued by the root, which the CA leaves out; that is where
        // "ISRG Root X1" shows up, and why an old device needs the cross-signed chain (T34).
        var root = top.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
        if (!string.Equals(root, expected.RootCommonName, StringComparison.Ordinal))
        {
            return $"The certificate chain does not end at the expected root ({expected.RootCommonName}).";
        }

        return null;
    }

    private static List<X509Certificate2> ParseChain(string pem)
    {
        var result = new List<X509Certificate2>();
        try
        {
            var span = pem.AsSpan();
            while (PemEncoding.TryFind(span, out var fields))
            {
                if (span[fields.Label].SequenceEqual("CERTIFICATE"))
                {
                    result.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(span[fields.Base64Data].ToString())));
                }

                span = span[fields.Location.End..];
            }
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return new List<X509Certificate2>();
        }

        return result;
    }

    private static StoredCertificate? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Ephemeral: loading must not leave key files in the account's key store each time we look.
            var all = X509CertificateLoader.LoadPkcs12Collection(File.ReadAllBytes(path), PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
            var leaf = all.FirstOrDefault(c => c.HasPrivateKey);
            if (leaf is null)
            {
                return null;
            }

            // A PFX does not promise to keep the order, so walk from the leaf up by issuer.
            var chain = new List<X509Certificate2> { leaf };
            var rest = all.Where(c => c.Thumbprint != leaf.Thumbprint).ToList();
            while (rest.Find(c => c.Subject == chain[^1].Issuer && c.Subject != c.Issuer) is { } next)
            {
                chain.Add(next);
                rest.Remove(next);
            }

            return new StoredCertificate(leaf, chain);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Fail open: a damaged file is the same as no certificate; the manager will issue a new one.
            return null;
        }
    }
}

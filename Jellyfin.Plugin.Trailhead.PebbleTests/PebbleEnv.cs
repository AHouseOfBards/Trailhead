using System.Security.Cryptography.X509Certificates;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.Trailhead.PebbleTests;

/// <summary>
/// Where Pebble and challtestsrv are, as tests/pebble.sh started them. Nothing is defaulted: a test run without the
/// script has no server to talk to, and has to fail saying so, not skip and look green.
/// </summary>
internal sealed record PebbleEnv(
    Uri Directory,
    Uri Management,
    Uri NonceRejectDirectory,
    Uri NonceRejectManagement,
    string CaPath,
    Uri ChallengeApi,
    string DnsHost,
    int DnsPort)
{
    internal const string DirectoryVar = "TRAILHEAD_PEBBLE_DIR";
    internal const string ManagementVar = "TRAILHEAD_PEBBLE_MGMT";
    internal const string NonceDirectoryVar = "TRAILHEAD_PEBBLE_NONCE_DIR";
    internal const string NonceManagementVar = "TRAILHEAD_PEBBLE_NONCE_MGMT";
    internal const string CaVar = "TRAILHEAD_PEBBLE_CA";
    internal const string ChallengeApiVar = "TRAILHEAD_CHALLTESTSRV_API";
    internal const string DnsVar = "TRAILHEAD_CHALLTESTSRV_DNS";

    internal static PebbleEnv Current => From(Environment.GetEnvironmentVariable);

    internal static PebbleEnv From(Func<string, string?> get)
    {
        var missing = new[] { DirectoryVar, ManagementVar, NonceDirectoryVar, NonceManagementVar, CaVar, ChallengeApiVar, DnsVar }
            .Where(name => string.IsNullOrWhiteSpace(get(name)))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Pebble is not set up (missing " + string.Join(", ", missing) + "). Run these tests with `sh tests/pebble.sh`; they never skip.");
        }

        var dns = get(DnsVar)!.Split(':');
        if (dns.Length != 2 || !int.TryParse(dns[1], out var dnsPort))
        {
            throw new InvalidOperationException(DnsVar + " must be host:port.");
        }

        return new PebbleEnv(
            new Uri(get(DirectoryVar)!),
            new Uri(get(ManagementVar)!),
            new Uri(get(NonceDirectoryVar)!),
            new Uri(get(NonceManagementVar)!),
            get(CaVar)!,
            new Uri(get(ChallengeApiVar)!),
            dns[0],
            dnsPort);
    }

    internal X509Certificate2 LoadCa() => X509Certificate2.CreateFromPem(File.ReadAllText(CaPath));
}

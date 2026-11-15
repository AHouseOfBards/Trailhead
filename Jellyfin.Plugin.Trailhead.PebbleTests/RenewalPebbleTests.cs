using System.Net;
using Campground.Shared.Security;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.PebbleTests;

/// <summary>
/// The whole renewal path as Jellyfin runs it: the scheduled task, <see cref="CertificateServices"/> building its
/// manager from saved settings, the Dynu client against the fake Dynu API, and Pebble as the CA. Only the endpoints
/// are replaced, through the services' internal overrides; everything between is the real code.
/// </summary>
public sealed class RenewalPebbleTests
{
    [Fact]
    public async Task The_task_issues_through_the_services_and_a_second_run_finds_it_up_to_date()
    {
        var env = PebbleEnv.Current;
        await using var dynu = await FakeDynu.StartAsync();
        using var rig = new Rig(env);
        using var folder = new TempFolder();
        var log = new ListLogger();
        dynu.AddDomain(rig.Name);
        dynu.OnTxtAdded = record => rig.Dns.SetTxtAsync(HostOf(record), record.Text, CancellationToken.None);
        dynu.OnTxtRemoved = record => rig.Dns.RemoveAsync(HostOf(record), CancellationToken.None);

        var box = new SecretBox(Enumerable.Repeat((byte)7, 32).ToArray());
        var settings = new ServiceSettings(rig.Name, new DynuKey(box).Seal(FakeDynu.Key));
        var root = await rig.RootCommonNameAsync();
        using var services = new CertificateServices(
            log,
            () => settings,
            new ServicesOverrides
            {
                Box = box,
                DataFolder = folder.File("data"),
                AcmeDirectory = env.Directory,
                ExtraCa = env.LoadCa(),
                DynuBase = dynu.BaseUrl,
                DnsServers = new[] { new IPEndPoint(IPAddress.Parse(env.DnsHost), env.DnsPort) },
                DnsWait = new DnsWaitOptions { Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromSeconds(30), QueryTimeout = TimeSpan.FromSeconds(2) },
                Issuer = new IssuerOptions { PollInterval = TimeSpan.FromMilliseconds(200), MaxPolls = 100 },

                // "default" is the profile Pebble offers; the services' own default is Let's Encrypt's "classic".
                Profile = "default",
                ExpectedRoot = root,
            });
        var task = new RenewCertificateTask(services, new PebbleTypedLogger<RenewCertificateTask>(log));

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var stored = new CertificateStore(folder.File("data/certs"), TimeProvider.System, log).Load();
        Assert.True(stored is not null, log.All);
        Assert.Contains("Trailhead got a new certificate", log.All);

        // The record went in through Dynu's API and is gone again, from Dynu and from the DNS server.
        Assert.Single(dynu.Requests, r => r.Method == "POST");
        Assert.Empty(dynu.Records);
        Assert.Equal(0, await rig.Dns.TxtCountAsync(rig.ChallengeHost));

        var issuedLines = log.All;
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        var afterSecond = log.All[issuedLines.Length..];
        Assert.Contains("Trailhead's certificate is up to date", afterSecond);
        Assert.DoesNotContain("got a new certificate", afterSecond);
        Assert.Single(dynu.Requests, r => r.Method == "POST");
        Assert.Equal(stored!.Leaf.Thumbprint, new CertificateStore(folder.File("data/certs"), TimeProvider.System, log).Load()!.Leaf.Thumbprint);

        Assert.DoesNotContain(FakeDynu.Key, log.All);
        Assert.DoesNotContain(FakeDynu.Token, log.All);
    }

    private static string HostOf(FakeDynuRecord record) => record.NodeName.Length == 0 ? record.DomainName : record.NodeName + "." + record.DomainName;

    private sealed class PebbleTypedLogger<T> : ILogger<T>
    {
        private readonly ILogger _inner;

        internal PebbleTypedLogger(ILogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

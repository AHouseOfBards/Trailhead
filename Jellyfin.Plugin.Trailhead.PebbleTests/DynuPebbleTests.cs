using System.Net;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.PebbleTests;

/// <summary>
/// The Dynu parts against Pebble and challtestsrv. The Dynu API is the fake (a real Kestrel server) and nothing here
/// talks to the real one; the DNS server is challtestsrv, which Pebble reads TXT records from.
/// </summary>
public sealed class DynuPebbleTests
{
    private static IPEndPoint DnsServer(PebbleEnv env) => new(IPAddress.Parse(env.DnsHost), env.DnsPort);

    [Fact]
    public async Task The_waiter_waits_until_challtestsrv_serves_the_value_and_then_returns()
    {
        var env = PebbleEnv.Current;
        using var rig = new Rig(env);
        var log = new ListLogger();
        var waiter = AuthoritativeDnsWaiter.Create(
            log,
            TimeProvider.System,
            new DnsWaitOptions { Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromSeconds(30), QueryTimeout = TimeSpan.FromSeconds(2) },
            new[] { DnsServer(env) });
        var host = rig.ChallengeHost;

        var task = waiter.WaitAsync(host, "the-right-value", CancellationToken.None);

        // Nothing is served yet, and then only a different value is: still waiting.
        await Task.Delay(500);
        Assert.False(task.IsCompleted, log.All);
        await rig.Dns.SetTxtAsync(host, "some-other-value", CancellationToken.None);
        await Task.Delay(500);
        Assert.False(task.IsCompleted, log.All);

        try
        {
            await rig.Dns.SetTxtAsync(host, "the-right-value", CancellationToken.None);
            await task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await rig.Dns.RemoveAsync(host, CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_waiter_given_no_servers_fails_instead_of_passing()
    {
        var waiter = AuthoritativeDnsWaiter.Create(new ListLogger(), TimeProvider.System, fixedServers: Array.Empty<IPEndPoint>());

        await Assert.ThrowsAsync<DnsWaitException>(() => waiter.WaitAsync("_acme-challenge.nowhere.example.com", "v", CancellationToken.None));
    }

    [Fact]
    public async Task A_certificate_is_issued_through_the_Dynu_challenge_and_the_record_is_gone_afterwards()
    {
        var env = PebbleEnv.Current;
        await using var dynu = await FakeDynu.StartAsync();
        DynuClient? client = null;
        using var rig = new Rig(
            env,
            challenge: r =>
            {
                dynu.AddDomain(r.Name);

                // Dynu's records are what challtestsrv serves, so Pebble sees them as it would see Dynu's own DNS.
                dynu.OnTxtAdded = record => r.Dns.SetTxtAsync(HostOf(record), record.Text, CancellationToken.None);
                dynu.OnTxtRemoved = record => r.Dns.RemoveAsync(HostOf(record), CancellationToken.None);
                client = DynuClient.Create(FakeDynu.Key, r.Log, r.Time, dynu.BaseUrl);
                var waiter = AuthoritativeDnsWaiter.Create(
                    r.Log,
                    r.Time,
                    new DnsWaitOptions { Interval = TimeSpan.FromMilliseconds(100), Timeout = TimeSpan.FromSeconds(30), QueryTimeout = TimeSpan.FromSeconds(2) },
                    new[] { DnsServer(r.Env) });
                return new DynuChallenge(client, waiter, r.Log);
            });
        using var _ = client;
        var manager = await rig.NewManagerAsync();

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.True(result.Outcome == ReconcileOutcome.Issued, rig.Log.All);
        Assert.NotNull(rig.Store.Load());

        // It went through Dynu's API: one TXT record added on the account's domain, with the challenge's value.
        var posts = dynu.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Single(posts);
        Assert.Contains("\"nodeName\":\"_acme-challenge\"", posts[0].Body);
        Assert.Equal(FakeDynu.Key, posts[0].ApiKey);

        // And it is gone from both Dynu and the DNS server.
        Assert.Empty(dynu.Records);
        Assert.Contains(dynu.Requests, r => r.Method == "DELETE");
        Assert.Equal(0, await rig.Dns.TxtCountAsync(rig.ChallengeHost));
        Assert.DoesNotContain(FakeDynu.Key, rig.Log.All);
        Assert.DoesNotContain(FakeDynu.Token, rig.Log.All);
    }

    [Fact]
    public async Task A_Dynu_key_that_is_refused_fails_the_run_permanently_and_leaves_no_record()
    {
        var env = PebbleEnv.Current;
        await using var dynu = await FakeDynu.StartAsync();
        DynuClient? client = null;
        using var rig = new Rig(
            env,
            challenge: r =>
            {
                dynu.AddDomain(r.Name);
                client = DynuClient.Create("WRONG-FAKE-KEY", r.Log, r.Time, dynu.BaseUrl);
                return new DynuChallenge(client, AuthoritativeDnsWaiter.Create(r.Log, r.Time, fixedServers: new[] { DnsServer(r.Env) }), r.Log);
            });
        using var _ = client;
        var manager = await rig.NewManagerAsync();

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(FailureKind.Permanent, result.Kind);
        Assert.Null(rig.Store.Load());
        Assert.Empty(dynu.Records);
        Assert.Equal(0, await rig.Dns.TxtCountAsync(rig.ChallengeHost));
    }

    private static string HostOf(FakeDynuRecord record) => record.NodeName.Length == 0 ? record.DomainName : record.NodeName + "." + record.DomainName;
}

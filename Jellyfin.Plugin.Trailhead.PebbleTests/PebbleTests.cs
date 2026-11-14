using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Jellyfin.Plugin.Trailhead.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.PebbleTests;

/// <summary>
/// The Acme component against Pebble v2.10.3, Let's Encrypt's own test server, with DNS-01 answered by challtestsrv.
/// tests/pebble.sh starts both. Nothing here talks to Let's Encrypt.
/// </summary>
public sealed class PebbleTests
{
    private static IEnumerable<string> DnsNames(X509Certificate2 cert)
        => cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames());

    [Fact]
    public void Without_the_pebble_environment_the_harness_fails_instead_of_skipping()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PebbleEnv.From(_ => null));
        Assert.Contains(PebbleEnv.DirectoryVar, error.Message);
        Assert.Contains("never skip", error.Message);

        // And a half-set environment is no better than an empty one.
        Assert.Throws<InvalidOperationException>(() => PebbleEnv.From(name => name == PebbleEnv.DirectoryVar ? "https://localhost:1/dir" : null));
    }

    [Fact]
    public async Task Issuance_end_to_end_through_the_manager()
    {
        using var rig = new Rig(PebbleEnv.Current);
        var manager = await rig.NewManagerAsync();
        var changed = new List<StoredCertificate>();
        manager.CertificateChanged += changed.Add;

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.True(result.Outcome == ReconcileOutcome.Issued, rig.Log.All);
        var stored = rig.Store.Load();
        Assert.NotNull(stored);
        Assert.Contains(rig.Name, DnsNames(stored.Leaf));
        Assert.True(stored.Leaf.HasPrivateKey);
        Assert.Equal(2, stored.Chain.Count);
        Assert.Single(changed);
        Assert.Equal(new[] { rig.ChallengeHost }, rig.Dns.HostsSet);
        Assert.Equal(0, await rig.Dns.TxtCountAsync(rig.ChallengeHost));

        // Idempotent: a fresh certificate is left alone.
        var again = await manager.ReconcileAsync(false, CancellationToken.None);
        Assert.Equal(ReconcileOutcome.UpToDate, again.Outcome);
        Assert.Single(changed);
    }

    [Fact]
    public async Task A_wrong_TXT_value_is_a_permanent_failure_and_the_budget_counts_it()
    {
        using var rig = new Rig(PebbleEnv.Current, budgetLimit: 1);
        rig.Dns.Mutate = value => "not-" + value;
        var manager = await rig.NewManagerAsync();

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(FailureKind.Permanent, result.Kind);
        Assert.Contains("unauthorized", result.Message);
        Assert.Null(rig.Store.Load());
        Assert.NotNull(rig.Budget.BlockedUntil(rig.Name));
        Assert.Equal(0, await rig.Dns.TxtCountAsync(rig.ChallengeHost));

        // The count is what stops a second try: the CA is not asked again.
        var ordersBefore = rig.Recorder.Requests.Count(r => r.Path == "/order-plz");
        var second = await manager.ReconcileAsync(false, CancellationToken.None);
        Assert.Equal(ReconcileOutcome.Blocked, second.Outcome);
        Assert.Equal(ordersBefore, rig.Recorder.Requests.Count(r => r.Path == "/order-plz"));
    }

    [Fact]
    public async Task Renewal_through_ARI_sends_replaces_for_the_old_certificate()
    {
        using var rig = new Rig(PebbleEnv.Current);
        var manager = await rig.NewManagerAsync();
        Assert.Equal(ReconcileOutcome.Issued, (await manager.ReconcileAsync(false, CancellationToken.None)).Outcome);
        var first = rig.Store.Load()!;
        var firstId = new CertificateChain(first.ChainPem()).GetRenewalInfoCertificateId();

        // Control: Pebble's own window for a fresh certificate is in the future, so nothing renews.
        Assert.Equal(ReconcileOutcome.UpToDate, (await manager.ReconcileAsync(false, CancellationToken.None)).Outcome);
        Assert.Single(rig.Recorder.Requests, r => r.Path == "/order-plz");

        // Pebble v2.10.3 wfe/wfe.go SetRenewalInfo: POST /set-renewal-info/ on the management port, with the
        // certificate's PEM and the ARI answer to give for it. It needs a User-Agent (Support.cs sets one).
        var past = DateTimeOffset.UtcNow.AddHours(-2);
        await rig.PostManagementAsync("/set-renewal-info/", new
        {
            Certificate = first.Leaf.ExportCertificatePem(),
            ARIResponse = "{\"suggestedWindow\":{\"start\":\"" + past.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"end\":\"" + past.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}}",
        });

        var renewed = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.True(renewed.Outcome == ReconcileOutcome.Issued, rig.Log.All);
        var orders = rig.Recorder.Requests.Where(r => r.Path == "/order-plz").ToList();
        Assert.Equal(2, orders.Count);
        Assert.DoesNotContain("replaces", orders[0].Payload);
        Assert.Contains("\"replaces\":\"" + firstId + "\"", orders[1].Payload);
        Assert.NotEqual(first.Leaf.Thumbprint, rig.Store.Load()!.Leaf.Thumbprint);
        Assert.Equal(first.Leaf.Thumbprint, rig.Store.LoadPrevious()!.Leaf.Thumbprint);
    }

    [Fact]
    public async Task Cancelling_mid_poll_removes_the_TXT_record()
    {
        using var rig = new Rig(PebbleEnv.Current);
        using var cts = new CancellationTokenSource();

        // Cancel the moment the CA has accepted the challenge: the record is published and the run is about to poll.
        // Pebble v2.10.3 wfe/wfe.go challengePath is "/chalZ/".
        rig.Recorder.OnResponse = (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.StartsWith("/chalZ/", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Issuer.IssueAsync(new IssueRequest(rig.Name, "default", null, null), cts.Token));

        Assert.Equal(new[] { rig.ChallengeHost }, rig.Dns.HostsSet);
        Assert.Equal(0, await rig.Dns.TxtCountAsync(rig.ChallengeHost));
    }

    [Fact]
    public async Task A_CA_that_rejects_half_the_nonces_still_issues()
    {
        // Four accounts, because one newAccount without our retry still gets through half the time.
        for (var i = 0; i < 4; i++)
        {
            using var rig = new Rig(PebbleEnv.Current, nonceReject: true);
            var manager = await rig.NewManagerAsync();

            var result = await manager.ReconcileAsync(false, CancellationToken.None);

            Assert.True(result.Outcome == ReconcileOutcome.Issued, rig.Log.All);
            Assert.NotNull(rig.Store.Load());
        }
    }

    [Fact]
    public async Task A_certificate_that_fails_the_store_check_leaves_the_old_one()
    {
        using var rig = new Rig(PebbleEnv.Current);
        Assert.Equal(ReconcileOutcome.Issued, (await (await rig.NewManagerAsync()).ReconcileAsync(false, CancellationToken.None)).Outcome);
        var before = rig.Store.Load()!.Leaf.Thumbprint;

        // Same CA, a new certificate, but we expect another root: the check refuses it after it has been issued.
        var strict = await rig.NewManagerAsync("Some Other Root");
        var result = await strict.ReconcileAsync(true, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(FailureKind.Permanent, result.Kind);
        Assert.Contains("expected root", result.Message);
        Assert.Equal(2, rig.Recorder.Requests.Count(r => r.Path == "/order-plz"));
        Assert.Equal(before, rig.Store.Load()!.Leaf.Thumbprint);
    }
}

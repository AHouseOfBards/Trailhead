using System.Security.Cryptography;
using Certes;
using Certes.Acme;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class CertificateManagerTests : IDisposable
{
    private const string Name = "home.example.org";
    private const string Root = "Test Root X1";
    private readonly TempFolder _folder = new();
    private readonly ListLogger _log = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _folder.Dispose();

    private sealed class FakeIssuer(Func<IssueRequest, Task<IssueResult>> issue) : ICertificateIssuer
    {
        internal int Calls;
        internal List<IssueRequest> Requests { get; } = new();
        internal RenewalInfo? Info { get; set; }

        public TimeSpan? LastRetryAfter { get; set; }

        public Task<IssueResult> IssueAsync(IssueRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Requests)
            {
                Requests.Add(request);
            }

            return issue(request);
        }

        public Task<RenewalInfo?> GetRenewalInfoAsync(string chainPem, CancellationToken ct) => Task.FromResult(Info);
    }

    private IssueResult Good()
    {
        var now = _time.GetUtcNow();
        var (pem, key, _) = TestPki.Issue(Name, Root, now.AddHours(-1), now.AddDays(90));
        return new IssueResult(pem, key);
    }

    private (CertificateManager Manager, FakeIssuer Issuer, FailureBudget Budget, CertificateStore Store) Build(Func<IssueRequest, Task<IssueResult>>? issue = null)
    {
        var store = new CertificateStore(_folder.File("certs"), _time, _log);
        var issuer = new FakeIssuer(issue ?? (_ => Task.FromResult(Good())));
        var budget = new FailureBudget(_folder.File("budget.json"), _time);
        var options = new ManagerOptions(Name, "classic", Root, new CertificateExpectation(Name, Root));
        return (new CertificateManager(store, issuer, budget, _time, _log, options), issuer, budget, store);
    }

    [Fact]
    public async Task Two_concurrent_callers_give_one_issuance()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var (manager, issuer, _, store) = Build(async _ =>
        {
            started.TrySetResult();
            await gate.Task;
            return Good();
        });

        var first = manager.ReconcileAsync(false, CancellationToken.None);
        await started.Task;
        var second = manager.ReconcileAsync(false, CancellationToken.None);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, issuer.Calls);
        Assert.All(results, r => Assert.Equal(ReconcileOutcome.Issued, r.Outcome));
        Assert.NotNull(store.Load());
    }

    [Fact]
    public async Task Running_again_does_only_what_is_still_needed()
    {
        var (manager, issuer, _, _) = Build();
        var raised = 0;
        manager.CertificateChanged += _ => raised++;

        var first = await manager.ReconcileAsync(false, CancellationToken.None);
        var second = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Issued, first.Outcome);
        Assert.Equal(ReconcileOutcome.UpToDate, second.Outcome);
        Assert.Equal(1, issuer.Calls);
        Assert.Equal(1, raised);
        // Valid from an hour ago for 90 days from now: renew when a third of that span is left.
        var notBefore = _time.GetUtcNow().AddHours(-1);
        var notAfter = _time.GetUtcNow().AddDays(90);
        Assert.Equal(notAfter - ((notAfter - notBefore) / 3), second.NextCheck);
        Assert.Equal("classic", issuer.Requests[0].Profile);
        Assert.Null(issuer.Requests[0].ReplacesCertId);
    }

    [Fact]
    public async Task Renews_when_due_and_says_which_certificate_it_replaces()
    {
        var (manager, issuer, _, store) = Build();
        await manager.ReconcileAsync(false, CancellationToken.None);
        var oldThumb = store.Load()!.Leaf.Thumbprint;
        issuer.Info = new RenewalInfo("CERT-ID-1", new RenewalWindow(_time.GetUtcNow().AddDays(60), _time.GetUtcNow().AddDays(61)), null);
        _time.Advance(TimeSpan.FromDays(61));

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Issued, result.Outcome);
        Assert.Equal("CERT-ID-1", issuer.Requests[1].ReplacesCertId);
        Assert.NotEqual(oldThumb, store.Load()!.Leaf.Thumbprint);
        Assert.Equal(oldThumb, store.LoadPrevious()!.Leaf.Thumbprint);
    }

    [Fact]
    public async Task An_earlier_ARI_window_brings_renewal_forward()
    {
        var (manager, issuer, _, _) = Build();
        await manager.ReconcileAsync(false, CancellationToken.None);
        issuer.Info = new RenewalInfo("CERT-ID-1", new RenewalWindow(_time.GetUtcNow().AddDays(-1), _time.GetUtcNow().AddHours(-2)), null);

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Issued, result.Outcome);
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    public async Task Force_issues_even_when_the_certificate_is_fine()
    {
        var (manager, issuer, _, _) = Build();
        await manager.ReconcileAsync(false, CancellationToken.None);

        var result = await manager.ReconcileAsync(true, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Issued, result.Outcome);
        Assert.Equal(2, issuer.Calls);
    }

    [Fact]
    public async Task A_name_over_budget_waits_instead_of_trying()
    {
        var (manager, issuer, budget, _) = Build();
        for (var i = 0; i < 3; i++)
        {
            budget.RecordFailure(Name);
        }

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Blocked, result.Outcome);
        Assert.Equal(_time.GetUtcNow().AddHours(1), result.NextCheck);
        Assert.Equal(0, issuer.Calls);
    }

    [Fact]
    public async Task A_failed_validation_is_permanent_and_counts_against_the_budget()
    {
        var (manager, _, budget, store) = Build(_ => throw new AuthorizationInvalidException(Name, "urn:ietf:params:acme:error:unauthorized", "wrong TXT"));

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(FailureKind.Permanent, result.Kind);
        Assert.Null(store.Load());
        for (var i = 0; i < 2; i++)
        {
            await manager.ReconcileAsync(false, CancellationToken.None);
        }

        Assert.NotNull(budget.BlockedUntil(Name));
    }

    [Fact]
    public async Task A_rate_limit_is_transient_and_waits_for_its_Retry_After()
    {
        var (manager, issuer, budget, _) = Build(_ => throw new AcmeRequestException("slow down", new AcmeError { Type = "urn:ietf:params:acme:error:rateLimited", Status = (System.Net.HttpStatusCode)429 }));
        issuer.LastRetryAfter = TimeSpan.FromMinutes(7);

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(FailureKind.Transient, result.Kind);
        Assert.Equal(_time.GetUtcNow().AddMinutes(7), result.NextCheck);
        Assert.Null(budget.BlockedUntil(Name));
    }

    [Fact]
    public async Task An_unexpected_exception_is_a_result_not_a_throw()
    {
        var (manager, _, _, _) = Build(_ => throw new InvalidOperationException("boom"));

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Equal(FailureKind.Transient, result.Kind);
        Assert.NotNull(result.NextCheck);
    }

    [Fact]
    public async Task A_certificate_that_fails_the_store_check_is_a_failure_and_changes_nothing()
    {
        var (manager, _, _, store) = Build(_ =>
        {
            var now = _time.GetUtcNow();
            var (pem, key, _) = TestPki.Issue("other.example.org", Root, now.AddHours(-1), now.AddDays(90));
            return Task.FromResult(new IssueResult(pem, key));
        });

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, result.Outcome);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_undo_or_fail_the_issuance()
    {
        var (manager, _, _, store) = Build();
        manager.CertificateChanged += _ => throw new InvalidOperationException("listener broke");

        var result = await manager.ReconcileAsync(false, CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Issued, result.Outcome);
        Assert.NotNull(store.Load());
    }

    [Fact]
    public async Task Cancellation_is_the_only_thing_that_throws()
    {
        var (manager, issuer, _, _) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ReconcileAsync(false, cts.Token));
        Assert.Equal(0, issuer.Calls);
    }

    [Fact]
    public async Task A_caller_that_cancels_does_not_cancel_the_other_caller()
    {
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var (manager, issuer, _, _) = Build(async _ =>
        {
            started.TrySetResult();
            await gate.Task;
            return Good();
        });
        using var cts = new CancellationTokenSource();

        var first = manager.ReconcileAsync(false, CancellationToken.None);
        await started.Task;
        var second = manager.ReconcileAsync(false, cts.Token);
        await cts.CancelAsync();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(ReconcileOutcome.Issued, (await first).Outcome);
        Assert.Equal(1, issuer.Calls);
    }
}

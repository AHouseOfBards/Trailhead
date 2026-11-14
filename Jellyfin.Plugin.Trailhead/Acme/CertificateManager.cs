using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Acme;

internal sealed record IssueRequest(string Name, string? Profile, string? ReplacesCertId, string? PreferredRoot);

/// <param name="RootCommonName">Whose root signed the last certificate of the chain, as served; null if the chain had no issuers.</param>
internal sealed record IssueResult(string ChainPem, RSA Key, string? RootCommonName = null);

internal sealed record RenewalInfo(string CertId, RenewalWindow Window, TimeSpan? RetryAfter);

internal interface ICertificateIssuer
{
    /// <summary>The Retry-After of the last error response from the CA, if it sent one.</summary>
    TimeSpan? LastRetryAfter { get; }

    Task<IssueResult> IssueAsync(IssueRequest request, CancellationToken ct);

    /// <summary>The CA's renewal window for this chain, or null when the CA doesn't offer ARI.</summary>
    Task<RenewalInfo?> GetRenewalInfoAsync(string chainPem, CancellationToken ct);
}

internal sealed record ManagerOptions(string Name, string? Profile, string? PreferredRoot, CertificateExpectation Expected);

internal enum ReconcileOutcome
{
    UpToDate,
    Issued,
    Blocked,
    Failed,
}

internal sealed record ReconcileResult(ReconcileOutcome Outcome, DateTimeOffset? NextCheck, FailureKind? Kind, string? Message);

/// <summary>
/// Brings the stored certificate to what it should be: asks the policy whether it is time, checks the failure
/// budget, issues, checks and stores. Running it again does only what is still needed. It reports problems as
/// results; only cancellation throws, because it can run in the background and a failure there must never reach Jellyfin.
/// </summary>
internal sealed class CertificateManager
{
    private static readonly TimeSpan TransientRetry = TimeSpan.FromMinutes(15);

    private readonly CertificateStore _store;
    private readonly ICertificateIssuer _issuer;
    private readonly FailureBudget _budget;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ManagerOptions _options;
    private readonly object _gate = new();
    private Task<ReconcileResult>? _inflight;

    internal CertificateManager(CertificateStore store, ICertificateIssuer issuer, FailureBudget budget, TimeProvider time, ILogger logger, ManagerOptions options)
    {
        _store = store;
        _issuer = issuer;
        _budget = budget;
        _time = time;
        _logger = logger;
        _options = options;
    }

    /// <summary>Raised after a new certificate is stored. A listener that throws is logged and ignored.</summary>
    internal event Action<StoredCertificate>? CertificateChanged;

    /// <summary>
    /// One reconcile at a time. A caller that arrives while one is running waits for that one's result instead of
    /// starting a second issuance (two would spend the failed-validation allowance twice and race on the TXT record).
    /// A waiter can cancel without cancelling the run; if the run's own caller cancels, a waiter takes over.
    /// </summary>
    internal async Task<ReconcileResult> ReconcileAsync(bool force, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task<ReconcileResult> running;
            lock (_gate)
            {
                // Task.Run so the run never executes inside the lock, and its cleanup can't race the assignment.
                running = _inflight ??= Task.Run(() => RunThenClearAsync(force, ct), CancellationToken.None);
            }

            try
            {
                return await running.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The caller that started the run cancelled it; try again as the one to run.
            }
        }
    }

    private async Task<ReconcileResult> RunThenClearAsync(bool force, CancellationToken ct)
    {
        try
        {
            return await RunAsync(force, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inflight = null;
            }
        }
    }

    private async Task<ReconcileResult> RunAsync(bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var stored = _store.Load();
        if (stored is not null && !CoversName(stored))
        {
            // Left over from another name: replace it, but it is nothing to say we are "replacing" at the CA.
            stored = null;
        }

        string? replaces = null;
        if (stored is not null)
        {
            var info = await TryGetRenewalInfoAsync(stored, ct).ConfigureAwait(false);
            var decision = RenewalPolicy.Decide(stored.Leaf.NotBefore.ToUniversalTime(), stored.Leaf.NotAfter.ToUniversalTime(), info?.Window, _time.GetUtcNow(), RenewalPolicy.FractionFor(stored.Leaf.Thumbprint));
            if (!force && decision is RenewalDecision.CheckAt at)
            {
                // Look again when the CA says its window may have moved, if that is sooner.
                var next = info?.RetryAfter is { } retry && _time.GetUtcNow() + retry < at.Time ? _time.GetUtcNow() + retry : at.Time;
                return new ReconcileResult(ReconcileOutcome.UpToDate, next, null, null);
            }

            replaces = info?.CertId;
        }

        if (_budget.BlockedUntil(_options.Name) is { } blockedUntil)
        {
            _logger.LogWarning("Trailhead is waiting until {Until:u} before trying {Name} again (failed validations per hour limit).", blockedUntil, _options.Name);
            return new ReconcileResult(ReconcileOutcome.Blocked, blockedUntil, null, "Too many failed validations; waiting.");
        }

        try
        {
            var issued = await _issuer.IssueAsync(new IssueRequest(_options.Name, _options.Profile, replaces, _options.PreferredRoot), ct).ConfigureAwait(false);
            using (issued.Key)
            {
                var stored2 = _store.Install(issued.ChainPem, issued.Key, _options.Expected);
                if (!stored2.Ok)
                {
                    return new ReconcileResult(ReconcileOutcome.Failed, null, FailureKind.Permanent, stored2.Reason);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return Failed(e);
        }

        var current = _store.Load();
        if (current is not null)
        {
            RaiseChanged(current);
        }

        var after = current is null ? (DateTimeOffset?)null : (RenewalPolicy.Decide(current.Leaf.NotBefore.ToUniversalTime(), current.Leaf.NotAfter.ToUniversalTime(), null, _time.GetUtcNow(), 0) as RenewalDecision.CheckAt)?.Time;
        return new ReconcileResult(ReconcileOutcome.Issued, after, null, null);
    }

    private ReconcileResult Failed(Exception e)
    {
        var kind = FailureBudget.Classify(e);
        if (e is AuthorizationInvalidException)
        {
            _budget.RecordFailure(_options.Name);
        }

        // Type names and the CA's problem type only: never an exception's own text, which could carry anything.
        var what = e switch
        {
            Certes.AcmeRequestException { Error: { } error } => $"{error.Type}: {error.Detail}",
            AuthorizationInvalidException invalid => $"{invalid.ProblemType}: {invalid.Detail}",
            _ => e.GetType().Name,
        };
        _logger.LogWarning("Trailhead could not get a certificate for {Name} ({Kind}): {What}", _options.Name, kind, what);

        DateTimeOffset? next = kind == FailureKind.Transient ? _time.GetUtcNow() + (_issuer.LastRetryAfter ?? TransientRetry) : null;
        return new ReconcileResult(ReconcileOutcome.Failed, next, kind, what);
    }

    private bool CoversName(StoredCertificate stored)
        => stored.Leaf.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames()).Contains(_options.Name, StringComparer.OrdinalIgnoreCase);

    private async Task<RenewalInfo?> TryGetRenewalInfoAsync(StoredCertificate stored, CancellationToken ct)
    {
        try
        {
            return await _issuer.GetRenewalInfoAsync(stored.ChainPem(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // ARI is an optimisation. Without it the one-third rule still renews in time.
            _logger.LogInformation("Trailhead could not ask the CA for a renewal window ({Error}); using the one-third rule.", e.GetType().Name);
            return null;
        }
    }

    private void RaiseChanged(StoredCertificate current)
    {
        try
        {
            CertificateChanged?.Invoke(current);
        }
        catch (Exception e)
        {
            _logger.LogWarning("A listener for the new certificate failed ({Error}).", e.GetType().Name);
        }
    }
}

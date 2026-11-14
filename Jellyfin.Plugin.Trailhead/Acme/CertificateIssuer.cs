using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Acme;

internal sealed class IssuerOptions
{
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    internal int MaxPolls { get; init; } = 90;

    /// <summary>How long clean-up of the TXT record may take once the run is over, cancelled or not.</summary>
    internal TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Certes retries badNonce itself for orders, but not for newAccount, which is where 50% rejection hurt (spike
    /// T-S2). Tests raise it so a Pebble that rejects half the nonces can't fail a run by bad luck.
    /// </summary>
    internal int NewAccountAttempts { get; init; } = 5;
}

/// <summary>
/// One issuance against a CA, written around the eight things Certes 5.0.0-beta.1 gets wrong or leaves to us
/// (spike T-S2, acme-pebble-spike.md). Each is marked where it is handled.
/// </summary>
internal sealed class CertificateIssuer : ICertificateIssuer
{
    private const string BadNonce = "urn:ietf:params:acme:error:badNonce";

    private readonly IAcmeContext _ctx;
    private readonly IDnsChallenge _dns;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan?> _retryAfter;
    private readonly IssuerOptions _options;

    internal CertificateIssuer(IAcmeContext ctx, IDnsChallenge dns, TimeProvider time, ILogger logger, Func<TimeSpan?> lastRetryAfter, IssuerOptions? options = null)
    {
        _ctx = ctx;
        _dns = dns;
        _time = time;
        _logger = logger;
        _retryAfter = lastRetryAfter;
        _options = options ?? new IssuerOptions();
    }

    public TimeSpan? LastRetryAfter => _retryAfter();

    /// <summary>A Certes context that talks through our handler's HttpClient and keeps Certes's own badNonce retry at 5.</summary>
    internal static AcmeContext NewContext(Uri directory, IKey accountKey, HttpClient client, int badNonceRetries = 5)
        => new(directory, accountKey, new AcmeHttpClient(directory, client), badNonceRetries);

    public async Task<IssueResult> IssueAsync(IssueRequest request, CancellationToken ct)
    {
        await EnsureAccountAsync(ct).ConfigureAwait(false);

        var order = await NewOrderAsync(request, ct).ConfigureAwait(false);
        await ValidateAuthorizationsAsync(order, ct).ConfigureAwait(false);

        var ready = await PollOrderAsync(order, s => s == OrderStatus.Pending, ct).ConfigureAwait(false);
        if (ready.Status != OrderStatus.Ready)
        {
            throw new AcmeException("The order is " + ready.Status + " before finalize.");
        }

        using var csr = new CsrBuilder();
        var finalized = await order.Finalize(csr.BuildDer(new[] { request.Name }), ct).ConfigureAwait(false);
        if (finalized.Status != OrderStatus.Valid)
        {
            finalized = await PollOrderAsync(order, s => s is OrderStatus.Processing or OrderStatus.Pending or OrderStatus.Ready, ct).ConfigureAwait(false);
        }

        if (finalized.Status != OrderStatus.Valid)
        {
            throw new AcmeException("The order is " + finalized.Status + " after finalize.");
        }

        var chain = await order.Download(request.PreferredRoot, ct).ConfigureAwait(false);
        var root = RootOf(chain);

        // Workaround 5: a preferredChain that matches nothing silently gives the default chain, so look at what we got.
        if (request.PreferredRoot is not null && !string.Equals(root, request.PreferredRoot, StringComparison.Ordinal))
        {
            _logger.LogWarning("Trailhead asked for the chain ending at {Wanted} and the CA served one ending at {Got}.", request.PreferredRoot, root);
        }

        var pem = chain.Certificate.ToPem() + "\n" + string.Concat(chain.Issuers.Select(i => i.ToPem() + "\n"));

        // The CSR builder is disposed when we leave; the caller owns a copy of the key.
        return new IssueResult(pem, RSA.Create(csr.Key.ExportParameters(true)), root);
    }

    public async Task<RenewalInfo?> GetRenewalInfoAsync(string chainPem, CancellationToken ct)
    {
        var directory = await _ctx.GetDirectory(ct).ConfigureAwait(false);
        if (directory.RenewalInfo is null)
        {
            return null;
        }

        var id = new CertificateChain(chainPem).GetRenewalInfoCertificateId();
        var info = await _ctx.GetRenewalInfo(id, ct).ConfigureAwait(false);
        return new RenewalInfo(id, new RenewalWindow(info.SuggestedWindow.Start, info.SuggestedWindow.End), info.RetryAfter);
    }

    private async Task EnsureAccountAsync(CancellationToken ct)
    {
        // Workaround 2: our own badNonce retry around newAccount (and so Account()), which Certes doesn't retry.
        // No contact address is sent: nothing in Trailhead has one to give, and the CA doesn't need it.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _ctx.NewAccount(Array.Empty<string>(), true, null, null, null, ct).ConfigureAwait(false);
                return;
            }
            catch (AcmeRequestException e) when (attempt < _options.NewAccountAttempts && e.Error?.Type == BadNonce)
            {
            }
        }
    }

    private async Task<IOrderContext> NewOrderAsync(IssueRequest request, CancellationToken ct)
    {
        var names = new List<string> { request.Name };
        if (request.Profile is not null)
        {
            // Workaround 6: Certes refuses an unadvertised profile before sending, with an exception that says little.
            // Look at the directory ourselves and fall back to the CA's default.
            var directory = await _ctx.GetDirectory(ct).ConfigureAwait(false);
            if (directory.Meta?.Profiles?.ContainsKey(request.Profile) == true)
            {
                return await _ctx.NewOrderWithProfile(names, request.Profile, null, null, request.ReplacesCertId, false, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("The CA doesn't offer the {Profile} profile; using its default.", request.Profile);
        }

        return request.ReplacesCertId is not null
            ? await _ctx.NewReplacementOrder(names, request.ReplacesCertId, null, null, ct).ConfigureAwait(false)
            : await _ctx.NewOrder(names, null, null, ct).ConfigureAwait(false);
    }

    private async Task ValidateAuthorizationsAsync(IOrderContext order, CancellationToken ct)
    {
        var handles = new List<string>();
        try
        {
            foreach (var authorization in await order.Authorizations(ct).ConfigureAwait(false))
            {
                var resource = await authorization.Resource(ct).ConfigureAwait(false);

                // Workaround 4: the CA reuses a still-valid authorization for this account and name, and asking it
                // to validate again is a 400.
                if (resource.Status == AuthorizationStatus.Valid)
                {
                    continue;
                }

                var name = resource.Identifier.Value;
                var challenge = await authorization.Dns(ct).ConfigureAwait(false) ?? throw new AcmeException("The CA offered no dns-01 challenge for " + name + ".");

                // Workaround 1: DnsTxt wants the TOKEN and hashes the key authorization itself; handing it the key
                // authorization hashes twice and validation fails with "Correct value not found".
                var value = _ctx.AccountKey.DnsTxt(challenge.Token);
                var host = "_acme-challenge." + name;
                handles.Add(await _dns.SetTxtAsync(host, value, ct).ConfigureAwait(false));
                await _dns.WaitUntilVisibleAsync(host, value, ct).ConfigureAwait(false);
                await challenge.Validate(ct).ConfigureAwait(false);
                await WaitAuthorizationAsync(authorization, name, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await CleanUpAsync(handles).ConfigureAwait(false);
        }
    }

    private async Task CleanUpAsync(List<string> handles)
    {
        // Never leave the record behind, on success, failure or cancel, so this must not use the caller's token.
        // A failure here is logged, not thrown: it would hide the error that got us here.
        using var limit = new CancellationTokenSource(_options.CleanupTimeout, _time);
        foreach (var handle in handles)
        {
            try
            {
                await _dns.RemoveAsync(handle, limit.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning("Trailhead could not remove a DNS challenge record ({Error}). Remove it by hand.", e.GetType().Name);
            }
        }
    }

    private async Task WaitAuthorizationAsync(IAuthorizationContext authorization, string name, CancellationToken ct)
    {
        for (var i = 0; i < _options.MaxPolls; i++)
        {
            var resource = await authorization.Resource(ct).ConfigureAwait(false);
            if (resource.Status == AuthorizationStatus.Valid)
            {
                return;
            }

            if (resource.Status == AuthorizationStatus.Invalid)
            {
                // Workaround 7: an invalid authorization doesn't throw; the reason is in the challenge's error.
                var error = resource.Challenges?.Select(c => c.Error).FirstOrDefault(e => e is not null);
                throw new AuthorizationInvalidException(name, error?.Type, error?.Detail);
            }

            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
        }

        throw new TimeoutException("The authorization for " + name + " stayed pending.");
    }

    private async Task<Order> PollOrderAsync(IOrderContext order, Func<OrderStatus?, bool> keepWaiting, CancellationToken ct)
    {
        for (var i = 0; i < _options.MaxPolls; i++)
        {
            var resource = await order.Resource(ct).ConfigureAwait(false);
            if (!keepWaiting(resource.Status))
            {
                return resource;
            }

            await Task.Delay(_options.PollInterval, _time, ct).ConfigureAwait(false);
        }

        throw new TimeoutException("The order did not settle.");
    }

    private static string? RootOf(CertificateChain chain)
    {
        var last = chain.Issuers.LastOrDefault();
        return last is null ? null : X509CertificateLoader.LoadCertificate(last.ToDer()).GetNameInfo(X509NameType.SimpleName, forIssuer: true);
    }
}

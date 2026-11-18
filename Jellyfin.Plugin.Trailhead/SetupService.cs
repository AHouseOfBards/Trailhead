using Campground.Shared.I18n;
using Campground.Shared.Security;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Dns;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>Why a setup step didn't give its normal answer. The controller turns each into a status and a code.</summary>
internal enum SetupFailure
{
    None,

    /// <summary>The key's shape is wrong (empty, too long, spaces or control characters). Nothing was sent anywhere.</summary>
    KeyInvalid,

    /// <summary>Dynu said no to the key that was just entered.</summary>
    KeyRejected,

    /// <summary>Dynu said no to the key that is already saved.</summary>
    BadKey,

    /// <summary>No key is saved, or the saved one can't be opened here.</summary>
    KeyMissing,

    Unavailable,

    /// <summary>A setting was written but didn't read back the same.</summary>
    SaveFailed,
}

internal enum RateKind
{
    KeySave,
    NameCheck,
    NameClaim,
}

internal sealed record ParentInfo(string Name, bool IsDefault);

internal sealed record RenewalStatus(string State, string? Outcome, DateTimeOffset? At);

/// <summary>What the page needs to draw itself. It never holds the key, sealed or not.</summary>
internal sealed record SetupState(string DynuKey, string Hostname, IReadOnlyList<ParentInfo> Parents, RenewalStatus Renewal);

internal sealed record KeyOutcome(SetupFailure Failure, bool NameCleared = false, SetupState? Setup = null);

internal sealed record NameOutcome(SetupFailure Failure, NameClaim? Claim = null, IReadOnlyList<string>? Alternatives = null, SetupState? Setup = null);

/// <summary>
/// The work behind Trailhead's setup routes, so the controller stays thin. It is built from the same
/// <see cref="ServicesOverrides"/> as <see cref="CertificateServices"/>, which is how a test points it at FakeDynu.
/// Nothing here does I/O until a route is called.
/// </summary>
public class SetupService : IDisposable
{
    internal const int MaxKeyLength = 256;

    /// <summary>How many other endings a taken name is offered under. The page has a picker for the rest.</summary>
    internal const int AlternativeCount = 3;

    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ListLife = TimeSpan.FromSeconds(30);

    // One limiter per kind, counted per server: a setup page has one admin at a time, and the key save tests a secret
    // against Dynu, so it gets the tightest limit.
    private const string LimitKey = "server";

    private readonly ILogger _logger;
    private readonly ServicesOverrides _overrides;
    private readonly IApplicationPaths? _paths;
    private readonly CertificateServices? _certificates;
    private readonly TimeProvider _time;
    private readonly Lazy<ParentDomains> _parents = new(ParentDomains.Embedded, LazyThreadSafetyMode.PublicationOnly);
    private readonly Lazy<SecretBox> _box;
    private readonly RateLimiter _keyLimit;
    private readonly RateLimiter _checkLimit;
    private readonly RateLimiter _claimLimit;

    // Key and name writes take turns. This is the DNS lock of the design: two claims at once would both see the name
    // free, and one of them would then be told it is taken.
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly object _cacheGate = new();
    private (DateTimeOffset At, IReadOnlyList<DynuDomain> Domains)? _cache;
    private int _cacheGeneration;

    public SetupService(IApplicationPaths applicationPaths, CertificateServices certificates, ILogger<SetupService> logger)
        : this(logger, new ServicesOverrides(), applicationPaths, certificates)
    {
    }

    internal SetupService(ILogger logger, ServicesOverrides overrides, IApplicationPaths? paths = null, CertificateServices? certificates = null)
    {
        _logger = logger;
        _overrides = overrides;
        _paths = paths;
        _certificates = certificates;
        _time = overrides.Time ?? TimeProvider.System;
        _box = new Lazy<SecretBox>(OpenBox, LazyThreadSafetyMode.PublicationOnly);
        DateTime Now() => _time.GetUtcNow().UtcDateTime;
        _keyLimit = new RateLimiter(10, TimeSpan.FromMinutes(10), Now);
        _checkLimit = new RateLimiter(60, TimeSpan.FromMinutes(1), Now);
        _claimLimit = new RateLimiter(10, TimeSpan.FromMinutes(1), Now);
    }

    /// <summary>The string tables error bodies and the strings route answer from.</summary>
    internal Strings Strings => _overrides.Strings ?? UiText.Table;

    public void Dispose()
    {
        _writes.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Counts an attempt before any of its work is done, so parallel requests can't slip past the limit. When it
    /// refuses, <paramref name="retryAfterSeconds"/> is how long to wait.
    /// </summary>
    internal virtual bool TryBegin(RateKind kind, out int retryAfterSeconds)
    {
        var limiter = kind switch
        {
            RateKind.KeySave => _keyLimit,
            RateKind.NameCheck => _checkLimit,
            _ => _claimLimit,
        };

        retryAfterSeconds = 0;
        if (limiter.TryBegin(LimitKey))
        {
            return true;
        }

        retryAfterSeconds = int.Parse(limiter.RetryAfterHeader(LimitKey), System.Globalization.CultureInfo.InvariantCulture);
        return false;
    }

    internal virtual SetupState GetState()
    {
        var saved = ConfigGate.Read();
        var parents = _parents.Value.Offered.Select(p => new ParentInfo(p.Name, p.IsDefault)).ToList();
        return new SetupState(KeyStateOf(saved.DynuKey), saved.Hostname, parents, RenewalNow());
    }

    /// <summary>
    /// Checks a key against Dynu and, only if Dynu accepts it, seals and saves it. A new key on a different account
    /// clears a saved name the account doesn't have, because that name could never be renewed.
    /// </summary>
    internal virtual async Task<KeyOutcome> SaveKeyAsync(string? key, CancellationToken ct)
    {
        if (!KeyShapeOk(key, out var plain))
        {
            return new KeyOutcome(SetupFailure.KeyInvalid);
        }

        return await Budgeted(ct, async t =>
        {
            await _writes.WaitAsync(t).ConfigureAwait(false);
            try
            {
                return await SaveKeyLockedAsync(plain, t).ConfigureAwait(false);
            }
            finally
            {
                _writes.Release();
            }
        }, new KeyOutcome(SetupFailure.Unavailable)).ConfigureAwait(false);
    }

    /// <summary>Looks a name up without creating it. Saves nothing.</summary>
    internal virtual async Task<NameOutcome> CheckNameAsync(string? label, string? parent, CancellationToken ct)
    {
        if (NameClaimer.Validate(label, parent, _parents.Value) is { } invalid)
        {
            return Answer(invalid, withState: false);
        }

        if (!TryOpenKey(out var plain))
        {
            return new NameOutcome(SetupFailure.KeyMissing);
        }

        return await Budgeted(ct, async t =>
        {
            using var dynu = NewClient(plain);
            var claimer = new NameClaimer(dynu, _parents.Value, _logger, d => ListCachedAsync(dynu, d));
            return Answer(await claimer.CheckAsync(label, parent, t).ConfigureAwait(false), withState: false);
        }, new NameOutcome(SetupFailure.Unavailable)).ConfigureAwait(false);
    }

    /// <summary>Creates the name (or finds it) and saves it as the address. Safe to repeat.</summary>
    internal virtual async Task<NameOutcome> ClaimNameAsync(string? label, string? parent, CancellationToken ct)
    {
        if (NameClaimer.Validate(label, parent, _parents.Value) is { } invalid)
        {
            return Answer(invalid, withState: true);
        }

        if (!TryOpenKey(out var plain))
        {
            return new NameOutcome(SetupFailure.KeyMissing);
        }

        return await Budgeted(ct, async t =>
        {
            await _writes.WaitAsync(t).ConfigureAwait(false);
            try
            {
                return await ClaimLockedAsync(label, parent, plain, t).ConfigureAwait(false);
            }
            finally
            {
                _writes.Release();
            }
        }, new NameOutcome(SetupFailure.Unavailable)).ConfigureAwait(false);
    }

    internal static bool KeyShapeOk(string? key, out string plain)
    {
        plain = (key ?? string.Empty).Trim();

        // Printable ASCII only. Dynu's keys are, and a stray character here would otherwise reach the HTTP header
        // check, whose error text quotes the value.
        return plain.Length is > 0 and <= MaxKeyLength && plain.All(c => c is > ' ' and < (char)127);
    }

    private async Task<KeyOutcome> SaveKeyLockedAsync(string plain, CancellationToken ct)
    {
        using var dynu = NewClient(plain);
        var current = ConfigGate.Read();
        IReadOnlyList<DynuDomain>? domains = null;
        try
        {
            if (!await dynu.CheckKeyAsync(ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Dynu did not accept the key that was entered. Nothing was saved.");
                return new KeyOutcome(SetupFailure.KeyRejected);
            }

            if (current.Hostname.Length > 0 || current.NameCreatedByTrailhead.Length > 0 || current.PendingName.Length > 0)
            {
                domains = await dynu.ListDomainsAsync(ct).ConfigureAwait(false);
            }
        }
        catch (DynuException e)
        {
            _logger.LogInformation("Dynu could not check the key just now (status {Status}). Nothing was saved.", e.Status);
            return new KeyOutcome(SetupFailure.Unavailable);
        }

        var dynuKey = new DynuKey(_box.Value);
        var sealedKey = dynuKey.Seal(plain);
        var cleared = false;
        PluginConfiguration saved;
        try
        {
            saved = ConfigGate.Update(c =>
            {
                c.DynuKey = sealedKey;
                if (domains is null)
                {
                    return;
                }

                bool OnAccount(string name) => name.Length > 0 && domains.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

                // A name on another account can't be renewed with this key, and Remove must not go looking for it here.
                if (c.Hostname.Length > 0 && !OnAccount(c.Hostname))
                {
                    c.Hostname = string.Empty;
                    cleared = true;
                }

                c.NameCreatedByTrailhead = OnAccount(c.NameCreatedByTrailhead) ? c.NameCreatedByTrailhead : string.Empty;
                c.PendingName = OnAccount(c.PendingName) ? c.PendingName : string.Empty;
            });
        }
        catch (ConfigSaveException e)
        {
            _logger.LogWarning("Trailhead could not save the Dynu key: {Reason}", e.Message);
            return new KeyOutcome(SetupFailure.SaveFailed);
        }

        // What counts is that the saved value opens to the key that was entered, not that the write didn't throw.
        if (!dynuKey.TryOpen(saved.DynuKey, out var opened) || !string.Equals(opened, plain, StringComparison.Ordinal))
        {
            _logger.LogWarning("The Dynu key was saved but does not open to the key that was entered.");
            return new KeyOutcome(SetupFailure.SaveFailed);
        }

        ClearCache();
        _logger.LogInformation("Trailhead saved a new Dynu key.");
        if (cleared)
        {
            _logger.LogInformation("The saved web address is not on that Dynu account, so Trailhead cleared it. Choose a name again.");
        }

        return new KeyOutcome(SetupFailure.None, cleared, GetState());
    }

    private async Task<NameOutcome> ClaimLockedAsync(string? label, string? parent, string plain, CancellationToken ct)
    {
        var name = HostLabel.Build(label, parent, _parents.Value).Name!;

        // Recorded before the create goes out: if the answer is lost, this is how a later run knows the name may be ours.
        var previousPending = ConfigGate.Read().PendingName;
        try
        {
            ConfigGate.Update(c => c.PendingName = name);
        }
        catch (ConfigSaveException e)
        {
            _logger.LogWarning("Trailhead could not record the name before creating it: {Reason}", e.Message);
            return new NameOutcome(SetupFailure.SaveFailed);
        }

        using var dynu = NewClient(plain);
        var claim = await new NameClaimer(dynu, _parents.Value, _logger).ClaimAsync(label, parent, ct).ConfigureAwait(false);
        ClearCache();

        switch (claim.Kind)
        {
            case ClaimKind.Claimed:
            case ClaimKind.Ours:
                // Ours means Trailhead made it only if an earlier try was left in doubt for this very name. A name the
                // owner added in Dynu's panel stays theirs, so Remove never deletes it.
                var made = claim.Kind == ClaimKind.Claimed || string.Equals(previousPending, name, StringComparison.OrdinalIgnoreCase);
                try
                {
                    ConfigGate.Update(c =>
                    {
                        c.Hostname = name;
                        c.PendingName = string.Empty;
                        if (made)
                        {
                            c.NameCreatedByTrailhead = name;
                        }
                    });
                }
                catch (ConfigSaveException e)
                {
                    _logger.LogWarning("Trailhead made the name but could not save it: {Reason}", e.Message);
                    return new NameOutcome(SetupFailure.SaveFailed);
                }

                break;

            case ClaimKind.Unavailable:
                // The create may have gone through, so the record stays.
                break;

            default:
                // Nothing was created. Put back whatever was in doubt before this try.
                try
                {
                    ConfigGate.Update(c => c.PendingName = string.Equals(previousPending, name, StringComparison.OrdinalIgnoreCase) ? string.Empty : previousPending);
                }
                catch (ConfigSaveException e)
                {
                    _logger.LogWarning("Trailhead could not clear the name record: {Reason}", e.Message);
                }

                break;
        }

        return Answer(claim, withState: true);
    }

    private NameOutcome Answer(NameClaim claim, bool withState)
    {
        switch (claim.Kind)
        {
            case ClaimKind.BadKey:
                return new NameOutcome(SetupFailure.BadKey);
            case ClaimKind.Unavailable:
                return new NameOutcome(SetupFailure.Unavailable);
            default:
                return new NameOutcome(SetupFailure.None, claim, claim.Alternatives?.Take(AlternativeCount).ToList(), withState ? GetState() : null);
        }
    }

    // Runs work inside the setup budget. A blown budget is the same to the caller as Dynu not answering; a caller that
    // went away (ct) is still a cancel.
    private async Task<T> Budgeted<T>(CancellationToken ct, Func<CancellationToken, Task<T>> work, T onBudget)
    {
        using var timer = new CancellationTokenSource(_overrides.SetupBudget ?? DefaultBudget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timer.IsCancellationRequested)
        {
            _logger.LogInformation("A Dynu step took longer than Trailhead's time limit and was stopped.");
            return onBudget;
        }
    }

    private DynuClient NewClient(string plainKey)
        => DynuClient.Create(plainKey, _logger, _time, _overrides.DynuBase, _overrides.DynuInteractive ?? new DynuClientOptions { MaxAttempts = 2, RequestTimeout = TimeSpan.FromSeconds(10) });

    private async Task<IReadOnlyList<DynuDomain>> ListCachedAsync(DynuClient dynu, CancellationToken ct)
    {
        int generation;
        lock (_cacheGate)
        {
            if (_cache is { } hit && _time.GetUtcNow() - hit.At < ListLife)
            {
                return hit.Domains;
            }

            generation = _cacheGeneration;
        }

        var fresh = await dynu.ListDomainsAsync(ct).ConfigureAwait(false);
        lock (_cacheGate)
        {
            // A claim or a new key while this was in flight made the answer stale before it arrived.
            if (generation == _cacheGeneration)
            {
                _cache = (_time.GetUtcNow(), fresh);
            }
        }

        return fresh;
    }

    private void ClearCache()
    {
        lock (_cacheGate)
        {
            _cache = null;
            _cacheGeneration++;
        }
    }

    private string KeyStateOf(string saved)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return "None";
        }

        try
        {
            return new DynuKey(_box.Value).TryOpen(saved, out _) ? "Saved" : "Reenter";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A key file that can't be read is the same to the admin as one that won't open: type the key again.
            _logger.LogWarning("Trailhead could not open its key store ({ErrorType}).", e.GetType().Name);
            return "Reenter";
        }
    }

    private bool TryOpenKey(out string plain)
    {
        plain = string.Empty;
        var saved = ConfigGate.Read().DynuKey;
        if (string.IsNullOrWhiteSpace(saved))
        {
            return false;
        }

        try
        {
            if (new DynuKey(_box.Value).TryOpen(saved, out var opened))
            {
                plain = opened!;
                return true;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning("Trailhead could not open its key store ({ErrorType}).", e.GetType().Name);
        }

        return false;
    }

    private RenewalStatus RenewalNow()
    {
        var last = _certificates?.LastReport;
        return last is null
            ? new RenewalStatus("NotRunYet", null, null)
            : new RenewalStatus(last.Report.State.ToString(), last.Report.Result?.Outcome.ToString(), last.At);
    }

    private SecretBox OpenBox()
    {
        if (_overrides.Box is { } box)
        {
            return box;
        }

        if (_paths is not null)
        {
            CampgroundPaths.Initialize(_paths);
        }

        return SecretBox.ForPurpose("trailhead");
    }
}

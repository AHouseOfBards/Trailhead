using System.Net;
using Campground.Shared.Storage;
using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Network;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>What one address sync did. Codes, not text.</summary>
internal enum SyncOutcome
{
    /// <summary>No name or no key yet: nothing to keep in step.</summary>
    NotSetUp,

    /// <summary>Dynu's record already has this connection's address.</summary>
    Unchanged,

    /// <summary>Dynu had another address (or none), and now has this one.</summary>
    Updated,

    /// <summary>None of the lookup services gave a public IPv4 address.</summary>
    NoPublicAddress,

    /// <summary>The saved name isn't on the Dynu account any more.</summary>
    NameGone,

    /// <summary>Dynu refused the saved key, or it can't be opened on this server.</summary>
    KeyRejected,

    /// <summary>Dynu didn't answer, or answered with an error.</summary>
    DynuFailed,
}

/// <summary>
/// The last sync, saved beside the health file so the grace window survives a restart. No address in it.
/// </summary>
/// <param name="FailingSince">When the first failure in the current run of failures happened; null while it works.</param>
/// <param name="Permanent">The failure needs the admin (key, name); it is a fault at once, not after the grace window (03 §3b).</param>
internal sealed record SyncStatus(SyncOutcome Outcome, DateTimeOffset At, DateTimeOffset? FailingSince, bool Permanent)
{
    internal bool Failing => Outcome is not (SyncOutcome.NotSetUp or SyncOutcome.Unchanged or SyncOutcome.Updated);

    /// <summary>A failure that has outlasted <see cref="AddressSync.Grace"/>, or one that waiting can't fix.</summary>
    internal bool IsFault(DateTimeOffset now) => Failing && (Permanent || (FailingSince is { } since && now - since >= AddressSync.Grace));
}

/// <summary>
/// Keeps the name's A record on this connection's public IPv4 address (03 §2 Tasks; RASA <c>ddns</c>). It compares with
/// the record <b>Dynu holds</b>, read every run, never a copy kept here (A3): a failed update, or a change in Dynu's web
/// panel, would otherwise never be put right. It sends IPv4 only: no AAAA goes out before an outside IPv6 test exists
/// (T12, plan-finish choice 2), and an AAAA the admin set stays as Dynu has it.
/// </summary>
internal sealed class AddressSync
{
    /// <summary>A failing sync is a fault only after this long (03 §3b, "address update 1 h").</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    private readonly Func<PluginConfiguration> _config;
    private readonly Func<string, string?> _openKey;
    private readonly Func<string, DynuClient> _client;
    private readonly IPublicAddressLookup _lookup;
    private readonly AtomicJsonFile _state;
    private readonly PublicAddressWatch _watch;
    private readonly SemaphoreSlim _lock;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    internal AddressSync(Func<PluginConfiguration> config, Func<string, string?> openKey, Func<string, DynuClient> client, IPublicAddressLookup lookup, string statePath, PublicAddressWatch watch, SemaphoreSlim dnsLock, TimeProvider time, ILogger logger)
    {
        _config = config;
        _openKey = openKey;
        _client = client;
        _lookup = lookup;
        _state = new AtomicJsonFile(statePath);
        _watch = watch;
        _lock = dnsLock;
        _time = time;
        _logger = logger;
    }

    /// <summary>The last sync as saved. Null when there has been none, or the file can't be read.</summary>
    internal SyncStatus? Last
    {
        get
        {
            try
            {
                return _state.Load<SyncStatus>();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>One sync. Never throws except <see cref="OperationCanceledException"/> for <paramref name="ct"/>.</summary>
    internal async Task<SyncStatus> RunAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (outcome, permanent) = await SyncAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var before = Last;
            var failing = outcome is not (SyncOutcome.NotSetUp or SyncOutcome.Unchanged or SyncOutcome.Updated);
            DateTimeOffset? since = failing ? (before is { Failing: true, FailingSince: { } s } ? s : now) : null;
            var status = new SyncStatus(outcome, now, since, failing && permanent);
            try
            {
                _state.Save(status);
            }
            catch (Exception e)
            {
                // The sync itself happened; only its grace clock is lost, so the next failure starts a new one.
                _logger.LogWarning("Trailhead couldn't save the address check's result ({Error})", e.GetType().Name);
            }

            return status;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(SyncOutcome Outcome, bool Permanent)> SyncAsync(CancellationToken ct)
    {
        PluginConfiguration config;
        try
        {
            config = _config();
        }
        catch (Exception e)
        {
            _logger.LogWarning("Trailhead's address check couldn't read its settings ({Error})", e.GetType().Name);
            return (SyncOutcome.DynuFailed, false);
        }

        var hostname = config.Hostname?.Trim().TrimEnd('.') ?? string.Empty;
        if (hostname.Length == 0 || string.IsNullOrWhiteSpace(config.DynuKey))
        {
            return (SyncOutcome.NotSetUp, false);
        }

        string? key;
        try
        {
            key = _openKey(config.DynuKey);
        }
        catch (Exception)
        {
            key = null;
        }

        if (key is null)
        {
            return (SyncOutcome.KeyRejected, true);
        }

        PublicAddresses found;
        try
        {
            found = await _lookup.LookupAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Trailhead's address check couldn't look up the public address ({Error})", e.GetType().Name);
            found = new PublicAddresses(null, null);
        }

        // Only V4 is read. V6 is never sent (T12), whatever the lookup found.
        if (found.V4 is not { } v4)
        {
            return (SyncOutcome.NoPublicAddress, false);
        }

        _watch.SetOwn(v4);
        using var dynu = _client(key);
        try
        {
            var domain = (await dynu.ListDomainsAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase));
            if (domain is null)
            {
                _logger.LogWarning("Trailhead's name isn't on the Dynu account any more, so its address can't be kept up to date");
                return (SyncOutcome.NameGone, true);
            }

            var held = await dynu.GetDomainAsync(domain.Id, ct).ConfigureAwait(false);
            if (!held.Name.Equals(hostname, StringComparison.OrdinalIgnoreCase))
            {
                // Dynu answered for another name. Writing it back under ours would be a guess.
                _logger.LogWarning("Dynu gave back a different name for Trailhead's record, so its address was left alone");
                return (SyncOutcome.DynuFailed, false);
            }

            if (v4.Equals(held.PublishedIPv4))
            {
                _logger.LogDebug("Trailhead's address is up to date on Dynu");
                return (SyncOutcome.Unchanged, false);
            }

            await dynu.UpdateIPv4Async(held, v4, ct).ConfigureAwait(false);
            _logger.LogInformation("Trailhead pointed its name at this connection's new public address");
            return (SyncOutcome.Updated, false);
        }
        catch (DynuException e) when (e.AuthRejected)
        {
            _logger.LogWarning("Dynu refused Trailhead's key during the address check");
            return (SyncOutcome.KeyRejected, true);
        }
        catch (DynuException e)
        {
            // The message is built by DynuClient from the method, path and status only.
            _logger.LogInformation("Trailhead's address check didn't finish: {Reason}", e.Message);
            return (SyncOutcome.DynuFailed, e.Kind == FailureKind.Permanent);
        }
    }
}

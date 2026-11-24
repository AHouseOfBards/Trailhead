using System.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Router;

internal enum MapOutcome
{
    /// <summary>Mapped and read back, permanent.</summary>
    Mapped,

    /// <summary>Mapped and read back, with a lease the lease task keeps renewing. The page says so (T38).</summary>
    MappedWithLease,

    /// <summary>The router couldn't map 443 to the internal port, so external = internal was mapped instead (T16, A2).</summary>
    Remapped,

    /// <summary>The port goes to another device, and the fallback was taken too or isn't allowed. Never taken over.</summary>
    Conflict,

    /// <summary>The port already goes to this machine's internal port by a forward Trailhead didn't make. Left alone, not recorded.</summary>
    AlreadyForwarded,

    /// <summary>No router answered, or it has no port-mapping service.</summary>
    NoRouter,

    /// <summary>A router answered, but this machine has no address on its subnet (a second NAT, a Docker bridge, a VPN-only route).</summary>
    NoLanAddress,

    /// <summary>The router said yes but the read-back didn't show the mapping pointing here. Not mapped.</summary>
    NotConfirmed,

    /// <summary>The router refused for another reason, or didn't answer.</summary>
    Failed,
}

internal enum FallbackReason
{
    None,

    /// <summary>UPnP 724: this router only maps a port to the same port.</summary>
    SamePortRequired,

    /// <summary>443 already goes to another device on the network (a read showed it, or 718).</summary>
    TakenByOtherDevice,
}

/// <summary>What <see cref="RouterMapper.MapAsync"/> did. <see cref="Granted"/> is the read-back, never the request.</summary>
internal sealed record MapResult(MapOutcome Outcome, ForwardRecord? Forward = null, PortMapping? Granted = null, FallbackReason Reason = FallbackReason.None, int? UpnpCode = null, RouterStatus? Router = null);

internal enum ForwardOutcome
{
    /// <summary>In place, pointing here, nothing needed.</summary>
    Ok,

    /// <summary>A lease that was running down was renewed.</summary>
    Renewed,

    /// <summary>The router had lost it (a restart, a lapsed lease) and it was added again.</summary>
    Readded,

    /// <summary>It pointed at this machine's old address (DHCP moved it); it was moved to the new one.</summary>
    Moved,

    /// <summary>A pending record whose add never reached the router. Nothing to keep.</summary>
    Dropped,

    /// <summary>The port now goes to another device. Left alone: it isn't Trailhead's to take back.</summary>
    PointsElsewhere,

    /// <summary>No router answered this run.</summary>
    RouterMissing,

    /// <summary>The router answered with an error, or this machine had no address on its subnet.</summary>
    Failed,

    /// <summary>Deleted from the router (or it was already gone) and taken off the list.</summary>
    Released,

    /// <summary>The port now goes elsewhere, so nothing was deleted; taken off the list, since nothing of ours is left.</summary>
    LeftAlone,
}

/// <summary>One recorded forward after a renewal or release.</summary>
/// <param name="Fault">True when this is worth telling the admin: a definite problem, or a blip that outlasted the 6 h grace.</param>
internal sealed record ForwardReport(ForwardRecord Record, ForwardOutcome Outcome, bool Fault, int? UpnpCode = null, PortMapping? Seen = null);

internal sealed record RouterTarget(IgdDevice Device, IPAddress? LanAddress);

/// <summary>
/// The router half of setup and of the lease task, over the recorded-forwards list. Ported from RASA's
/// <c>portmap</c> (add, permanent then a lease, read back) and <c>portkeep</c> (rediscover every run, never take
/// another device's mapping, don't rewrite a permanent mapping every ten minutes, 6 h grace), with these differences:
/// <list type="bullet">
/// <item>A forward is recorded before the router is asked, so a crash between the two can still be cleaned up. RASA
/// recorded after, and an interrupted run left a mapping nothing knew about.</item>
/// <item>The read-back decides the result. RASA reported "mapped" from the request when the read-back failed.</item>
/// <item>Remapping to 8443 (724, or 443 taken) returns the port actually mapped, and the record holds it: there is one
/// place the external port lives (RASA B1 changed one copy and not the other).</item>
/// </list>
/// Every operation holds the one router lock (T26), shared by the wizard, the task and later ports.v1.
/// </summary>
internal sealed class RouterMapper
{
    internal const string TrailheadOwner = "trailhead";

    /// <summary>miniupnpd's ceiling, and so the longest lease most home routers will give (RASA, live).</summary>
    internal const int WeekLease = 604800;

    internal const int HourLease = 3600;

    /// <summary>How long a renewal may keep failing before it counts as a fault rather than a lost datagram (RASA portkeep).</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromHours(6);

    // Asked in this order. Permanent first: nothing to renew. Then a week, then an hour, for routers that cap lower.
    private static readonly int[] Leases = { 0, WeekLease, HourLease };

    // Three ten-minute task runs: a lease is renewed while there are still a couple of chances left.
    private static readonly TimeSpan RenewFloor = TimeSpan.FromMinutes(30);

    private readonly IRouterFinder _finder;
    private readonly Func<IPAddress, IPAddress?> _lanFor;
    private readonly Func<IgdService, IgdClient> _client;
    private readonly ForwardsStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock;
    private readonly ILogger _logger;

    internal RouterMapper(IRouterFinder finder, Func<IPAddress, IPAddress?> lanFor, Func<IgdService, IgdClient> client, ForwardsStore store, TimeProvider time, SemaphoreSlim routerLock, ILogger logger)
    {
        _finder = finder;
        _lanFor = lanFor;
        _client = client;
        _store = store;
        _time = time;
        _lock = routerLock;
        _logger = logger;
    }

    internal IReadOnlyList<ForwardRecord> Recorded => _store.Load();

    /// <summary>
    /// Maps <paramref name="external"/> on the router to <paramref name="internalPort"/> on this machine. When
    /// external is 443 and the router can't remap it (724) or 443 goes to another device, maps external = internal
    /// instead and says so (<see cref="MapOutcome.Remapped"/>). Safe to run again: a mapping of ours that is in place
    /// is found and kept.
    /// </summary>
    internal async Task<MapResult> MapAsync(int external, int internalPort, string protocol, CancellationToken ct)
    {
        protocol = Protocols.Normalise(protocol);
        CheckPort(external);
        CheckPort(internalPort);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (target, status) = await LocateAsync(ct).ConfigureAwait(false);
            if (target is null)
            {
                return new MapResult(MapOutcome.NoRouter, Router: status);
            }

            if (target.LanAddress is null)
            {
                return new MapResult(MapOutcome.NoLanAddress, Router: status);
            }

            var client = _client(target.Device.Service);
            var lan = target.LanAddress.ToString();
            var allowFallback = external == 443 && internalPort != 443;
            var first = await MapOnceAsync(client, lan, external, internalPort, protocol, allowFallback, ct).ConfigureAwait(false);
            if (first.Reason == FallbackReason.None || first.Outcome != MapOutcome.Conflict && first.Outcome != MapOutcome.Failed)
            {
                return first with { Router = status };
            }

            _logger.LogInformation("The router couldn't send outside port {External} here ({Reason}), so Trailhead is mapping outside port {Internal} instead.", external, first.Reason, internalPort);
            var second = await MapOnceAsync(client, lan, internalPort, internalPort, protocol, false, ct).ConfigureAwait(false);
            return second.Outcome is MapOutcome.Mapped or MapOutcome.MappedWithLease
                ? second with { Outcome = MapOutcome.Remapped, Reason = first.Reason, Router = status }
                : second with { Reason = first.Reason, Router = status };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The lease task's run: rediscovers the router and this machine's address, then checks each recorded forward.
    /// Re-adds only what is lapsing or lost, never one that now points at another device. With nothing recorded it
    /// does nothing at all, not even a search.
    /// </summary>
    internal async Task<IReadOnlyList<ForwardReport>> RenewAsync(CancellationToken ct)
    {
        if (_store.Load().Count == 0)
        {
            return Array.Empty<ForwardReport>();
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Again under the lock: a Remove that ran first has emptied it.
            var records = _store.Load();
            if (records.Count == 0)
            {
                return Array.Empty<ForwardReport>();
            }

            var (target, _) = await LocateAsync(ct).ConfigureAwait(false);
            var reports = new List<ForwardReport>();
            foreach (var record in records)
            {
                reports.Add(target is null
                    ? new ForwardReport(record, ForwardOutcome.RouterMissing, PastGrace(record))
                    : await RenewOneAsync(target, record, ct).ConfigureAwait(false));
            }

            return reports;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Deletes recorded forwards from the router: one (<paramref name="id"/>) or all (null), as Remove does. A forward
    /// that now points at another device is left on the router. One that can't be checked stays on the list, so a
    /// later Remove can try again; nothing is deleted on a guess.
    /// </summary>
    internal async Task<IReadOnlyList<ForwardReport>> ReleaseAsync(string? id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = _store.Load().Where(r => id is null || r.Id == id).ToList();
            if (records.Count == 0)
            {
                return Array.Empty<ForwardReport>();
            }

            var (target, _) = await LocateAsync(ct).ConfigureAwait(false);
            var reports = new List<ForwardReport>();
            foreach (var record in records)
            {
                if (target is null)
                {
                    reports.Add(new ForwardReport(record, ForwardOutcome.RouterMissing, true));
                    continue;
                }

                reports.Add(await ReleaseOneAsync(_client(target.Device.Service), record, ct).ConfigureAwait(false));
            }

            return reports;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(RouterTarget? Target, RouterStatus Status)> LocateAsync(CancellationToken ct)
    {
        var found = await _finder.FindAsync(ct).ConfigureAwait(false);
        if (found.Status != RouterStatus.Found || found.Device is null)
        {
            return (null, found.Status);
        }

        return (new RouterTarget(found.Device, _lanFor(found.Device.Address)), found.Status);
    }

    private async Task<MapResult> MapOnceAsync(IgdClient client, string lan, int external, int internalPort, string protocol, bool allowFallback, CancellationToken ct)
    {
        var record = _store.Load().FirstOrDefault(r => r.ExternalPort == external && r.Protocol == protocol);
        PortMapping? existing;
        try
        {
            existing = await client.GetAsync(external, protocol, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouterTrouble(e, ct))
        {
            // Some routers can't read single entries. The add still answers 718 if the port is someone else's.
            existing = null;
        }

        if (existing is not null)
        {
            if (existing.PointsAt(lan) && existing.InternalPort == internalPort)
            {
                if (record is null)
                {
                    return new MapResult(MapOutcome.AlreadyForwarded, Granted: existing);
                }

                // Ours and in place: a repeated step (T39 Continue) keeps it rather than adding a second time.
                var kept = record with { State = ForwardStates.Active, InternalClient = lan, InternalPort = internalPort, LeaseSeconds = existing.LeaseSeconds, AddedAt = record.AddedAt ?? Now, ConfirmedAt = Now };
                _store.Put(kept);
                return new MapResult(existing.Permanent ? MapOutcome.Mapped : MapOutcome.MappedWithLease, kept, existing);
            }

            if (record is not null && IsOurs(existing, record))
            {
                // Our own forward, left pointing at the address this machine had before. Taken down and made again.
                try
                {
                    await client.DeleteAsync(external, protocol, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (IsRouterTrouble(e, ct))
                {
                    return new MapResult(MapOutcome.Failed, record, existing, UpnpCode: (e as UpnpException)?.Code);
                }
            }
            else
            {
                return Refused(FallbackReason.TakenByOtherDevice, allowFallback, existing, null);
            }
        }

        var pending = new ForwardRecord(record?.Id ?? NewId(), record?.Owner ?? TrailheadOwner, protocol, external, internalPort, lan, ForwardStates.Pending, 0, record?.RecordedAt ?? Now, record?.AddedAt, record?.ConfirmedAt);
        _store.Put(pending);

        var add = await AddWithLeasesAsync(client, pending, ct).ConfigureAwait(false);
        if (add.Code is { } code)
        {
            // The router answered no, so nothing was made; the record goes.
            _store.Remove(pending.Id);
            return code switch
            {
                UpnpCodes.Conflict => Refused(FallbackReason.TakenByOtherDevice, allowFallback, null, code),
                UpnpCodes.SamePortRequired when external != internalPort => Refused(FallbackReason.SamePortRequired, allowFallback, null, code),
                _ => new MapResult(MapOutcome.Failed, UpnpCode: code),
            };
        }

        if (add.Lease is null)
        {
            // No answer at all: the add may have landed. The pending record stays so renewal or Remove can find it.
            return new MapResult(MapOutcome.Failed, pending);
        }

        PortMapping? readBack;
        try
        {
            readBack = await client.GetAsync(external, protocol, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouterTrouble(e, ct))
        {
            // Accepted but not confirmed. Kept, so it is renewed or removed later, and reported as not confirmed.
            var unconfirmed = pending with { State = ForwardStates.Active, LeaseSeconds = add.Lease.Value, AddedAt = Now };
            _store.Put(unconfirmed);
            return new MapResult(MapOutcome.NotConfirmed, unconfirmed);
        }

        if (readBack is null || !readBack.PointsAt(lan) || readBack.InternalPort != internalPort)
        {
            // The router said OK and stored nothing (or something else). Nothing of ours is there to keep.
            _store.Remove(pending.Id);
            _logger.LogWarning("The router accepted the forward for port {External} but doesn't show it pointing here, so it isn't counted as open.", external);
            return new MapResult(MapOutcome.NotConfirmed, Granted: readBack);
        }

        var active = pending with { State = ForwardStates.Active, LeaseSeconds = readBack.LeaseSeconds, AddedAt = Now, ConfirmedAt = Now };
        _store.Put(active);
        _logger.LogInformation("The router now forwards outside port {External} to Trailhead ({Lease}).", external, readBack.Permanent ? "permanent" : readBack.LeaseSeconds + " s lease");
        return new MapResult(readBack.Permanent ? MapOutcome.Mapped : MapOutcome.MappedWithLease, active, readBack);
    }

    private static MapResult Refused(FallbackReason reason, bool allowFallback, PortMapping? seen, int? code)
        => new(MapOutcome.Conflict, Granted: seen, Reason: allowFallback ? reason : FallbackReason.None, UpnpCode: code);

    /// <summary>The lease ladder. Code set: the router answered no. Lease null and no code: it didn't answer.</summary>
    private async Task<(int? Lease, int? Code)> AddWithLeasesAsync(IgdClient client, ForwardRecord record, CancellationToken ct)
    {
        int? last = null;
        foreach (var lease in Leases)
        {
            try
            {
                await client.AddAsync(record.ExternalPort, record.Protocol, record.InternalPort, record.InternalClient, lease, ct).ConfigureAwait(false);
                return (lease, null);
            }
            catch (UpnpException e) when (UpnpCodes.AsksForALease(e.Code))
            {
                last = e.Code;
            }
            catch (UpnpException e)
            {
                return (null, e.Code);
            }
            catch (Exception e) when (IsRouterTrouble(e, ct))
            {
                _logger.LogWarning("The router didn't answer a forward request ({Error}).", e.GetType().Name);
                return (null, null);
            }
        }

        return (null, last);
    }

    private async Task<ForwardReport> RenewOneAsync(RouterTarget target, ForwardRecord record, CancellationToken ct)
    {
        if (target.LanAddress is null)
        {
            return new ForwardReport(record, ForwardOutcome.Failed, PastGrace(record));
        }

        var client = _client(target.Device.Service);
        var lan = target.LanAddress.ToString();
        PortMapping? existing;
        try
        {
            existing = await client.GetAsync(record.ExternalPort, record.Protocol, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouterTrouble(e, ct))
        {
            return new ForwardReport(record, ForwardOutcome.Failed, PastGrace(record), (e as UpnpException)?.Code);
        }

        if (existing is null)
        {
            if (record.State == ForwardStates.Pending)
            {
                // Its add never reached the router. Setup's Continue makes it again if it is still wanted.
                _store.Remove(record.Id);
                return new ForwardReport(record, ForwardOutcome.Dropped, false);
            }

            return await ReaddAsync(client, record with { InternalClient = lan }, ForwardOutcome.Readded, ct).ConfigureAwait(false);
        }

        if (existing.PointsAt(lan) && existing.InternalPort == record.InternalPort)
        {
            if (!existing.Permanent && Remaining(record, existing) <= RenewAt(record))
            {
                return await ReaddAsync(client, record with { InternalClient = lan }, ForwardOutcome.Renewed, ct).ConfigureAwait(false);
            }

            var fine = record with { State = ForwardStates.Active, InternalClient = lan, ConfirmedAt = Now };
            _store.Put(fine);
            return new ForwardReport(fine, ForwardOutcome.Ok, false, Seen: existing);
        }

        if (IsOurs(existing, record))
        {
            // DHCP moved this machine. Our forward now points at whoever holds the old address, which is worse than
            // none, so it is taken down and made again for the new one.
            try
            {
                await client.DeleteAsync(record.ExternalPort, record.Protocol, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsRouterTrouble(e, ct))
            {
                return new ForwardReport(record, ForwardOutcome.Failed, PastGrace(record), (e as UpnpException)?.Code, existing);
            }

            return await ReaddAsync(client, record with { InternalClient = lan }, ForwardOutcome.Moved, ct).ConfigureAwait(false);
        }

        _logger.LogWarning("Outside port {External} on the router now goes to another device. Trailhead leaves it alone.", record.ExternalPort);
        return new ForwardReport(record, ForwardOutcome.PointsElsewhere, true, Seen: existing);
    }

    private async Task<ForwardReport> ReaddAsync(IgdClient client, ForwardRecord record, ForwardOutcome success, CancellationToken ct)
    {
        // Written first, so the address it is about to point at is on record if Jellyfin stops mid-way.
        _store.Put(record);
        var add = await AddWithLeasesAsync(client, record, ct).ConfigureAwait(false);
        if (add.Lease is null)
        {
            var outcome = add.Code == UpnpCodes.Conflict ? ForwardOutcome.PointsElsewhere : ForwardOutcome.Failed;
            return new ForwardReport(record, outcome, outcome == ForwardOutcome.PointsElsewhere || PastGrace(record), add.Code);
        }

        PortMapping? readBack = null;
        try
        {
            readBack = await client.GetAsync(record.ExternalPort, record.Protocol, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouterTrouble(e, ct))
        {
        }

        if (readBack is null || !readBack.PointsAt(record.InternalClient) || readBack.InternalPort != record.InternalPort)
        {
            var noted = record with { State = ForwardStates.Active, LeaseSeconds = add.Lease.Value, AddedAt = Now };
            _store.Put(noted);
            return new ForwardReport(noted, ForwardOutcome.Failed, PastGrace(record), Seen: readBack);
        }

        var renewed = record with { State = ForwardStates.Active, LeaseSeconds = readBack.LeaseSeconds, AddedAt = Now, ConfirmedAt = Now };
        _store.Put(renewed);
        if (success == ForwardOutcome.Renewed)
        {
            _logger.LogDebug("Renewed the lease on outside port {External}.", record.ExternalPort);
        }
        else
        {
            _logger.LogInformation("Outside port {External} was {What} on the router.", record.ExternalPort, success == ForwardOutcome.Moved ? "moved to this machine's new address" : "added again");
        }

        return new ForwardReport(renewed, success, false, Seen: readBack);
    }

    private async Task<ForwardReport> ReleaseOneAsync(IgdClient client, ForwardRecord record, CancellationToken ct)
    {
        PortMapping? existing;
        try
        {
            existing = await client.GetAsync(record.ExternalPort, record.Protocol, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouterTrouble(e, ct))
        {
            return new ForwardReport(record, ForwardOutcome.Failed, true, (e as UpnpException)?.Code);
        }

        if (existing is null)
        {
            _store.Remove(record.Id);
            return new ForwardReport(record, ForwardOutcome.Released, false);
        }

        if (!IsOurs(existing, record))
        {
            _store.Remove(record.Id);
            _logger.LogInformation("Outside port {External} now goes to another device, so Trailhead left it on the router.", record.ExternalPort);
            return new ForwardReport(record, ForwardOutcome.LeftAlone, false, Seen: existing);
        }

        try
        {
            await client.DeleteAsync(record.ExternalPort, record.Protocol, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsRouterTrouble(e, ct))
        {
            return new ForwardReport(record, ForwardOutcome.Failed, true, (e as UpnpException)?.Code, existing);
        }

        _store.Remove(record.Id);
        _logger.LogInformation("Removed the router forward for outside port {External}.", record.ExternalPort);
        return new ForwardReport(record, ForwardOutcome.Released, false);
    }

    /// <summary>
    /// A mapping is ours when it points where we pointed it, at the internal port we asked for, and carries our
    /// description (or none: some routers don't keep one). Anything else is left alone.
    /// </summary>
    private static bool IsOurs(PortMapping seen, ForwardRecord record)
        => seen.PointsAt(record.InternalClient) && seen.InternalPort == record.InternalPort
           && (seen.Description.Length == 0 || string.Equals(seen.Description, IgdClient.Description, StringComparison.OrdinalIgnoreCase));

    // The smaller of what the router says is left and what our own clock says: some routers report the lease they
    // granted every time rather than counting down, and a lease trusted that way would lapse unrenewed.
    private TimeSpan Remaining(ForwardRecord record, PortMapping seen)
    {
        var reported = TimeSpan.FromSeconds(seen.LeaseSeconds);
        if (record.AddedAt is not { } added || record.LeaseSeconds <= 0)
        {
            return reported;
        }

        var ours = TimeSpan.FromSeconds(record.LeaseSeconds) - (Now - added);
        return ours < reported ? ours : reported;
    }

    private static TimeSpan RenewAt(ForwardRecord record)
    {
        var half = TimeSpan.FromSeconds(record.LeaseSeconds / 2.0);
        return half > RenewFloor ? half : RenewFloor;
    }

    private bool PastGrace(ForwardRecord record) => Now - (record.ConfirmedAt ?? record.RecordedAt) > Grace;

    private DateTimeOffset Now => _time.GetUtcNow();

    private static bool IsRouterTrouble(Exception e, CancellationToken ct)
        => e is HttpRequestException or UpnpException or FormatException or InvalidDataException
           || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static void CheckPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "A port is 1 to 65535.");
        }
    }
}

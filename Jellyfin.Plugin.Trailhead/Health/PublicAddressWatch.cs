using System.Net;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>
/// This server's own public IPv4 address as the address sync last saw it, and when Edge last had a connection from it.
/// A connection from our own public address came from inside the house through the router (hairpin, 03 §3): it says
/// nothing about the outside, and a home app using the public name looks remote to Jellyfin because of it (T19).
/// In memory only; the address never goes to a file or a log.
/// </summary>
internal sealed class PublicAddressWatch
{
    private readonly TimeProvider _time;
    private IPAddress? _own;
    private long _seenTicks;
    private long _count;

    internal PublicAddressWatch(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    internal IPAddress? Own => Volatile.Read(ref _own);

    /// <summary>How many connections came from our own public address since Jellyfin started.</summary>
    internal long Count => Interlocked.Read(ref _count);

    internal DateTimeOffset? LastSeen
    {
        get
        {
            var ticks = Interlocked.Read(ref _seenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    internal void SetOwn(IPAddress? address) => Volatile.Write(ref _own, address is null ? null : Plain(address));

    /// <summary>True when <paramref name="remote"/> is our own public address. Counts it, so it is cheap enough for every request.</summary>
    internal bool IsOwn(IPAddress? remote)
    {
        var own = Own;
        if (own is null || remote is null || !own.Equals(Plain(remote)))
        {
            return false;
        }

        Interlocked.Increment(ref _count);
        Interlocked.Exchange(ref _seenTicks, _time.GetUtcNow().UtcTicks);
        return true;
    }

    private static IPAddress Plain(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
}

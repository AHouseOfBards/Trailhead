using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>The login throttle's numbers (T7, 03 §2 LoginThrottle row).</summary>
internal sealed class ThrottleOptions
{
    /// <summary>Failed sign-ins one address may make for one user name before each next try waits.</summary>
    internal int FreePerPair { get; init; } = 3;

    /// <summary>
    /// The looser ceiling for one address over every user name. A household behind one router, all signing in again
    /// after the T29 restart, mostly succeeds, so it stays far below this.
    /// </summary>
    internal int FreePerAddress { get; init; } = 20;

    /// <summary>The wait after the first failure past the free ones. It doubles with each failure after that.</summary>
    internal TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A key with no attempt for this long is forgotten.</summary>
    internal TimeSpan Forget { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Tries one pair may have waiting at once. One person never has more than one or two in flight; a guesser who
    /// opens hundreds of connections at once would otherwise all wait out the same delay and then go together.
    /// </summary>
    internal int MaxWaitingPerPair { get; init; } = 4;

    internal int MaxWaitingPerAddress { get; init; } = 16;

    /// <summary>Most keys held at once (each table). See <see cref="LoginThrottle"/> for what happens at the cap.</summary>
    internal int MaxKeys { get; init; } = 10_000;
}

/// <summary>Which Jellyfin sign-in route a request is, at v12.4.</summary>
internal enum SignInRoute
{
    None,

    /// <summary><c>POST /Users/AuthenticateByName</c> (Jellyfin.Api/Controllers/UserController.cs:209).</summary>
    ByName,

    /// <summary><c>POST /Users/{userId}/Authenticate?pw=</c>, obsolete but still served (UserController.cs:178).</summary>
    ById,

    /// <summary><c>POST /Users/AuthenticateWithQuickConnect</c> (UserController.cs:245).</summary>
    QuickConnect,
}

internal static class SignInRoutes
{
    /// <summary>
    /// The sign-in route a request is for. Jellyfin maps its whole API under the base URL (<c>app.Map(config.BaseUrl,
    /// …)</c>, Jellyfin.Server/Startup.cs:177, v12.4) and its routes are case-insensitive, so the base URL is taken
    /// off first, and the bare path is matched too in case a request reaches the API without it.
    /// </summary>
    internal static SignInRoute Match(string method, string? path, string? baseUrl, out string? userId)
    {
        userId = null;
        if (!HttpMethods.IsPost(method) || string.IsNullOrEmpty(path))
        {
            return SignInRoute.None;
        }

        var p = path.TrimEnd('/');
        var prefix = (baseUrl ?? string.Empty).TrimEnd('/');
        if (prefix.Length > 0 && p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            p = p[prefix.Length..];
        }

        if (p.Equals("/Users/AuthenticateByName", StringComparison.OrdinalIgnoreCase))
        {
            return SignInRoute.ByName;
        }

        if (p.Equals("/Users/AuthenticateWithQuickConnect", StringComparison.OrdinalIgnoreCase))
        {
            return SignInRoute.QuickConnect;
        }

        var parts = p.Split('/');
        if (parts.Length == 4 && parts[0].Length == 0 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals("Authenticate", StringComparison.OrdinalIgnoreCase) && parts[2].Length is > 0 and <= 64)
        {
            userId = parts[2].ToLowerInvariant();
            return SignInRoute.ById;
        }

        return SignInRoute.None;
    }

    /// <summary>
    /// What Jellyfin's answer means. 401 is a wrong name or password (SessionManager.cs:1745 throws
    /// AuthenticationException, which ExceptionMiddleware.cs:128 turns into 401), 403 a user not allowed in from that
    /// device or at their session limit (SessionManager.cs:1751-1759), and 404 an unknown user id or Quick Connect
    /// secret (UserController.cs:189, QuickConnectManager.cs:180). Anything else is neither: a 400 guesses nothing.
    /// </summary>
    internal static SignInOutcome Outcome(int status) => status switch
    {
        >= 200 and < 300 => SignInOutcome.Success,
        401 or 403 or 404 => SignInOutcome.Failure,
        _ => SignInOutcome.Neither,
    };
}

internal enum SignInOutcome
{
    Success,
    Failure,
    Neither,
}

/// <summary>
/// Slows down guessing on Jellyfin's sign-in routes (T7). It counts failed sign-ins only, keyed on the client's address
/// (an IPv6 address by its /64) plus the user name, with a looser ceiling for the address over every name. Past the
/// free tries each next try waits, twice as long each time up to a ceiling. It never refuses a sign-in for being
/// wrong too often, so a guesser on a shared carrier, hotel or school address can't lock someone else out (FieldGuide,
/// #17). A success clears that pair (a household behind one router, CleanStream #17).
/// </summary>
/// <remarks>
/// Shared <c>RateLimiter</c> was the first choice (03 §2). It refuses once a window is full, which is the lock-out
/// T7 rules out, and it has no idea of a delay that grows, so this is local. Its key cap is copied: at the cap the
/// least recently used key that isn't being slowed goes; a key being slowed is never pushed out, or a flood of new
/// names would free it. When every key is being slowed, a new one isn't tracked (the address key still is).
/// </remarks>
internal sealed class LoginThrottle
{
    private readonly ThrottleOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Table _pairs;
    private readonly Table _addresses;
    private DateTimeOffset _lastPrune;

    internal LoginThrottle(ThrottleOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _lastPrune = time.GetUtcNow();
        _pairs = new Table(options.MaxKeys, options.FreePerPair);
        _addresses = new Table(options.MaxKeys, options.FreePerAddress);
    }

    /// <summary>How many pairs are held (tests).</summary>
    internal int PairCount
    {
        get
        {
            lock (_gate)
            {
                return _pairs.Count;
            }
        }
    }

    /// <summary>The key for an address: IPv4 as it is, IPv6 by its /64, so one home's addresses count as one.</summary>
    internal static string AddressKey(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// Asks for a try. The ticket says how long to wait before sending it on, or that too many are waiting already.
    /// Every ticket that isn't refused must be finished with <see cref="Finish"/>.
    /// </summary>
    internal Ticket Enter(IPAddress client, string user)
    {
        var address = "a|" + AddressKey(client);
        var pair = "p|" + AddressKey(client) + "|" + (user ?? string.Empty).Trim().ToLowerInvariant();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            var a = _addresses.GetOrAdd(address, now);
            var p = _pairs.GetOrAdd(pair, now);
            if ((p?.Waiting ?? 0) >= _options.MaxWaitingPerPair || (a?.Waiting ?? 0) >= _options.MaxWaitingPerAddress)
            {
                var until = Max(p?.NextStart ?? now, a?.NextStart ?? now);
                return Ticket.Refused(Max(until - now, TimeSpan.FromSeconds(1)));
            }

            var start = Max(now, Max(p?.NextStart ?? now, a?.NextStart ?? now));
            Reserve(p, start, _options.FreePerPair);
            Reserve(a, start, _options.FreePerAddress);
            return new Ticket(this, address, pair, start - now);
        }
    }

    /// <summary>The wait before a try, after this many failures that count against the free ones.</summary>
    internal TimeSpan Delay(int failures, int free)
    {
        if (failures < free)
        {
            return TimeSpan.Zero;
        }

        var steps = Math.Min(failures - free, 30);
        var ticks = _options.FirstDelay.Ticks * Math.Pow(2, steps);
        return ticks >= _options.MaxDelay.Ticks ? _options.MaxDelay : TimeSpan.FromTicks((long)ticks);
    }

    private void Finish(string address, string pair, SignInOutcome outcome)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var (table, key, free) in new[] { (_pairs, pair, _options.FreePerPair), (_addresses, address, _options.FreePerAddress) })
            {
                if (table.Find(key) is not { } entry)
                {
                    continue;
                }

                entry.Waiting = Math.Max(0, entry.Waiting - 1);
                entry.LastSeen = now;
                switch (outcome)
                {
                    case SignInOutcome.Failure:
                        entry.Failures++;
                        break;
                    case SignInOutcome.Success when ReferenceEquals(table, _pairs):
                        table.Remove(key);
                        break;
                    default:
                        // Not a failure after all: the next try waits only as long as the failures so far say.
                        var fair = now + Delay(entry.Failures + entry.Waiting, free);
                        if (entry.NextStart > fair)
                        {
                            entry.NextStart = fair;
                        }

                        break;
                }
            }
        }
    }

    // The next try after this one waits as if this one fails: a guesser's tries in flight count before they end.
    private void Reserve(Entry? entry, DateTimeOffset start, int free)
    {
        if (entry is null)
        {
            return;
        }

        entry.Waiting++;
        entry.LastSeen = start;
        entry.NextStart = start + Delay(entry.Failures + entry.Waiting, free);
    }

    // A walk of both tables, so at most once a minute; a key that should have gone a little earlier costs nothing.
    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastPrune = now;
        _pairs.Prune(now, _options.Forget);
        _addresses.Prune(now, _options.Forget);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>One try: wait <see cref="Wait"/>, send it on, then <see cref="Finish"/> it with Jellyfin's answer.</summary>
    internal sealed class Ticket
    {
        private readonly LoginThrottle? _owner;
        private readonly string _address;
        private readonly string _pair;
        private int _finished;

        internal Ticket(LoginThrottle? owner, string address, string pair, TimeSpan wait)
        {
            _owner = owner;
            _address = address;
            _pair = pair;
            Wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        /// <summary>True when too many tries are waiting already; answer 429 with <see cref="Wait"/> as Retry-After.</summary>
        internal bool IsRefused => _owner is null;

        internal TimeSpan Wait { get; }

        internal static Ticket Refused(TimeSpan retryAfter) => new(null, string.Empty, string.Empty, retryAfter);

        /// <summary>Records how the try went. A second call does nothing.</summary>
        internal void Finish(SignInOutcome outcome)
        {
            if (_owner is not null && Interlocked.Exchange(ref _finished, 1) == 0)
            {
                _owner.Finish(_address, _pair, outcome);
            }
        }
    }

    private sealed class Entry
    {
        internal Entry(LinkedListNode<string> node) => Node = node;

        internal LinkedListNode<string> Node { get; }

        internal int Failures { get; set; }

        internal int Waiting { get; set; }

        internal DateTimeOffset NextStart { get; set; }

        internal DateTimeOffset LastSeen { get; set; }
    }

    private sealed class Table
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _byUse = new();
        private readonly int _max;
        private readonly int _free;

        internal Table(int max, int free)
        {
            _max = Math.Max(1, max);
            _free = free;
        }

        internal int Count => _entries.Count;

        internal Entry? Find(string key) => _entries.TryGetValue(key, out var e) ? e : null;

        // Null when the table is full of keys being slowed: that key goes untracked rather than free one of them.
        internal Entry? GetOrAdd(string key, DateTimeOffset now)
        {
            if (_entries.TryGetValue(key, out var found))
            {
                _byUse.Remove(found.Node);
                _byUse.AddLast(found.Node);
                return found;
            }

            if (_entries.Count >= _max && !MakeRoom())
            {
                return null;
            }

            var entry = new Entry(_byUse.AddLast(key)) { NextStart = now, LastSeen = now };
            _entries[key] = entry;
            return entry;
        }

        internal void Remove(string key)
        {
            if (_entries.Remove(key, out var e))
            {
                _byUse.Remove(e.Node);
            }
        }

        internal void Prune(DateTimeOffset now, TimeSpan forget)
        {
            var node = _byUse.First;
            while (node is not null)
            {
                var next = node.Next;
                var e = _entries[node.Value];
                if (e.Waiting == 0 && now - e.LastSeen >= forget)
                {
                    Remove(node.Value);
                }

                node = next;
            }
        }

        private bool MakeRoom()
        {
            for (var node = _byUse.First; node is not null; node = node.Next)
            {
                var e = _entries[node.Value];
                if (e.Waiting == 0 && e.Failures < _free)
                {
                    Remove(node.Value);
                    return true;
                }
            }

            return false;
        }
    }
}

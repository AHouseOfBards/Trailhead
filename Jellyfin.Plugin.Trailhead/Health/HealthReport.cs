using Jellyfin.Plugin.Trailhead.Edge;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>
/// How one check came out. <see cref="CouldNotTell"/> is its own state and never counts as OK (RASA <c>health</c>):
/// a check that couldn't run looks exactly like "all fine" otherwise.
/// </summary>
internal enum CheckState
{
    Ok,
    Problem,
    CouldNotTell,

    /// <summary>Nothing to check yet (not set up, or the router step was left for later).</summary>
    Off,
}

/// <summary>The check ids, in the order the page shows them.</summary>
internal static class CheckIds
{
    internal const string Certificate = "certificate";
    internal const string Listener = "listener";
    internal const string Settings = "settings";
    internal const string Router = "router";
    internal const string Address = "address";
    internal const string Outside = "outside";
}

/// <summary>
/// One check. <paramref name="Since"/> is when it went into this state with this code; <paramref name="Alerting"/> is
/// true once a problem has outlasted its grace window (03 §3b), and only then does it reach the activity log.
/// <paramref name="Facts"/> holds codes, dates and setting names: never an address or a secret.
/// </summary>
internal sealed record HealthCheck(string Id, CheckState State, string Code, DateTimeOffset? Since, bool Alerting, IReadOnlyDictionary<string, string>? Facts = null);

/// <summary>Edge has seen connections from this server's own public address: a home device going out and back in.</summary>
internal sealed record HairpinHint(DateTimeOffset LastSeen, long Count);

/// <summary>Overall answers. Never "ok" while any check couldn't tell.</summary>
internal static class Overall
{
    internal const string Ok = "ok";
    internal const string Problem = "problem";
    internal const string Unknown = "unknown";
    internal const string NotSetUp = "not_set_up";

    internal static string Of(IReadOnlyList<HealthCheck> checks)
    {
        var live = checks.Where(c => c.State != CheckState.Off).ToList();
        if (live.Count == 0)
        {
            return NotSetUp;
        }

        if (live.Any(c => c.State == CheckState.Problem))
        {
            return Problem;
        }

        return live.Any(c => c.State == CheckState.CouldNotTell) ? Unknown : Ok;
    }
}

/// <summary>
/// One health run, as saved to <c>health.json</c> and given to the page. The public address is a keyed hash (shared
/// <c>IpHasher</c>), so the file can say "it changed" without saying what it is.
/// </summary>
internal sealed record HealthReport(
    DateTimeOffset CheckedAt,
    string Overall,
    IReadOnlyList<HealthCheck> Checks,
    PhoneTestState? PhoneTest,
    HairpinHint? Hairpin,
    string? PublicAddressHash);

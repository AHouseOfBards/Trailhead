using System.Net;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>
/// How Edge listens and checks. The defaults are the design's (03 §3b, plan-finish choice 6); a test changes them
/// through the internal constructor of <see cref="EdgeService"/> only, so an admin can't.
/// </summary>
internal sealed class EdgeOptions
{
    /// <summary>The ports Edge may use, best first. 8443 is the one the router maps outside 443 to (T16).</summary>
    internal static readonly IReadOnlyList<int> DefaultPorts = new[] { 8443, 8444, 9443, 10443, 18443 };

    internal IReadOnlyList<int> Ports { get; init; } = DefaultPorts;

    /// <summary>Also listened on when the system allows it (T36). Null: no extra port.</summary>
    internal int? ExtraPort { get; init; } = 443;

    /// <summary>
    /// Null: every address, IPv6 and IPv4 on one dual-mode socket, or IPv4 alone when the system has no IPv6.
    /// Tests use loopback, so a test run never opens a port to the network.
    /// </summary>
    internal IPAddress? ListenAddress { get; init; }

    /// <summary>
    /// How long the reserved port is retried before a fallback port may be taken. After an in-process restart the old
    /// copy has let go of the port already (it is awaited on ApplicationStopping), but another process may be slow to.
    /// </summary>
    internal TimeSpan BindRetry { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan BindRetryStep { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the gate is checked again while it is open (drift closes it).</summary>
    internal TimeSpan RecheckOpen { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the gate is checked again while it is closed.</summary>
    internal TimeSpan RecheckClosed { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>What a refused client is told to wait.</summary>
    internal TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// YARP's idle timeout. Its default is 100 s, which would cut a paused stream; there is no overall timeout at all,
    /// because Jellyfin has none (03 §2, Edge row).
    /// </summary>
    internal TimeSpan ActivityTimeout { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The self-test's own limit for each of its two requests.</summary>
    internal TimeSpan SelfTestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The login throttle's numbers.</summary>
    internal ThrottleOptions Throttle { get; init; } = new();
}

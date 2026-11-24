using System.Net;
using System.Security.Cryptography;
using System.Text;
using Campground.Shared.Contracts;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>
/// What marks a request as Edge's for this start of Jellyfin: the marker Edge stamps on what it forwards, the token
/// its own self-test carries, and the address it forwards from. Both secrets are made once per copy of this service,
/// so an in-process restart makes new ones. They stay in this process: never in a response, a log, the page or a
/// support bundle (03 §8).
/// </summary>
public sealed class EdgeIdentity
{
    private IPAddress? _forwarding;
    private int _running;

    public EdgeIdentity()
    {
        Marker = NewSecret();
        ProbeToken = NewSecret();
    }

    /// <summary>The value of <see cref="HttpsClient.EdgeMarkerHeader"/> on every request Edge forwards.</summary>
    internal string Marker { get; }

    /// <summary>The value of the self-test's <see cref="EdgeTransformer.ProbeHeader"/>.</summary>
    internal string ProbeToken { get; }

    /// <summary>The address Edge connects to Jellyfin from, while it runs. Null when it isn't running.</summary>
    internal IPAddress? ForwardingAddress => Running ? Volatile.Read(ref _forwarding) : null;

    internal bool Running => Volatile.Read(ref _running) == 1;

    internal void Started(IPAddress forwarding)
    {
        Volatile.Write(ref _forwarding, forwarding);
        Volatile.Write(ref _running, 1);
    }

    internal void Stopped() => Volatile.Write(ref _running, 0);

    /// <summary>True when <paramref name="sent"/> is this start's probe token. Constant time.</summary>
    internal bool IsProbe(string? sent) => Same(sent, ProbeToken);

    /// <summary>The status a reader of the marker checks against, for a request reaching Jellyfin now.</summary>
    internal HttpsStatus? AsRunningStatus()
    {
        var forwarding = ForwardingAddress;
        return forwarding is null ? null : new HttpsStatus(true, null, new EdgeStatus(true, false, forwarding.ToString(), Marker));
    }

    private static bool Same(string? sent, string expected)
        => sent is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(expected));

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

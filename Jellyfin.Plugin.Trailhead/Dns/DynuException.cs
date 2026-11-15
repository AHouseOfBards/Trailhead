using Jellyfin.Plugin.Trailhead.Acme;

namespace Jellyfin.Plugin.Trailhead.Dns;

/// <summary>
/// A Dynu call failed. The message is built here from the method, the path, the status and Dynu's own error type, and
/// never from a body or a header, so it can't carry the API key or a hostname's update token.
/// </summary>
internal sealed class DynuException : Exception
{
    internal DynuException(FailureKind kind, string message, int status = 0, bool authRejected = false, bool fromEnvelope = false, string? errorType = null)
        : base(message)
    {
        Kind = kind;
        Status = status;
        AuthRejected = authRejected;
        FromEnvelope = fromEnvelope;
        ErrorType = errorType;
    }

    /// <summary>
    /// The status came out of Dynu's JSON body, not the HTTP status line. It tells Dynu's own 503 ("your account is
    /// full") from a gateway's 503 ("try again shortly").
    /// </summary>
    internal bool FromEnvelope { get; }

    /// <summary>Dynu's error category, such as "Argument Exception", when it looked like one. Never its free-text message.</summary>
    internal string? ErrorType { get; }

    /// <summary>Transient is worth another try later; permanent needs something to change (the key, the account, the name).</summary>
    internal FailureKind Kind { get; }

    /// <summary>The HTTP status, or the envelope's statusCode when Dynu reported the failure inside the body. 0 for network errors and timeouts.</summary>
    internal int Status { get; }

    /// <summary>Dynu said 401 or 403: the key is wrong, revoked or not allowed.</summary>
    internal bool AuthRejected { get; }
}

/// <summary>The wait for a TXT record ended without every authoritative nameserver serving it.</summary>
internal sealed class DnsWaitException : Exception
{
    internal DnsWaitException(string message)
        : base(message)
    {
    }
}

namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>Trailhead's side of DNS-01. Dynu implements it later; Pebble's challtestsrv in tests.</summary>
internal interface IDnsChallenge
{
    /// <summary>Publishes the TXT value and returns a handle for <see cref="RemoveAsync"/>.</summary>
    Task<string> SetTxtAsync(string name, string value, CancellationToken ct);

    Task RemoveAsync(string handle, CancellationToken ct);

    /// <summary>Returns once every authoritative nameserver serves the value.</summary>
    Task WaitUntilVisibleAsync(string name, string value, CancellationToken ct);
}

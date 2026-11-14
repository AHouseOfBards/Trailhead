namespace Jellyfin.Plugin.Trailhead.Acme;

/// <summary>
/// Let's Encrypt's directory URLs. Staging is the default: production allows only 5 failed validations per
/// hostname per hour, so it is used only when a caller asks for it by name.
/// </summary>
internal static class AcmeEndpoints
{
    internal static Uri Staging { get; } = new("https://acme-staging-v02.api.letsencrypt.org/directory");

    internal static Uri Production { get; } = new("https://acme-v02.api.letsencrypt.org/directory");

    internal static Uri Resolve(bool production = false) => production ? Production : Staging;
}

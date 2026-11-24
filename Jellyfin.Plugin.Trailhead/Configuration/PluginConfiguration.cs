using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailhead.Configuration;

/// <summary>
/// Marks a setting that only Trailhead's own routes may change. Jellyfin's generic save
/// (<c>POST /Plugins/{id}/Configuration</c>) leaves these as they are, so there is one way to set each.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class TrailheadOwnedAttribute : Attribute
{
}

/// <summary>Trailhead's saved settings. Empty until the setup flow has run.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The web address the certificate is for (a Dynu name). Empty until setup has claimed one.</summary>
    [TrailheadOwned]
    public string Hostname { get; set; } = string.Empty;

    /// <summary>The Dynu API key, sealed (a <c>cg1:</c> string). Never plain text; see Dns/DynuKey.cs.</summary>
    [TrailheadOwned]
    public string DynuKey { get; set; } = string.Empty;

    /// <summary>
    /// A name Trailhead is about to create on Dynu, written before the create goes out. If the answer is lost, this is
    /// how a later run knows the name may be Trailhead's. Empty when nothing is in doubt.
    /// </summary>
    [TrailheadOwned]
    public string PendingName { get; set; } = string.Empty;

    /// <summary>
    /// A name Trailhead itself made on the Dynu account, so Remove deletes only that and never a name the owner made.
    /// The undo journal takes this over when it exists.
    /// </summary>
    [TrailheadOwned]
    public string NameCreatedByTrailhead { get; set; } = string.Empty;

    /// <summary>
    /// The admin lets Trailhead ask check-host.net to connect to the name from outside (T9, T17). Off by default, and
    /// it does nothing until the code's own <c>OutsideCheckSwitch.CheckHostTermsConfirmed</c> is on too (plan-finish
    /// choice 1): the checker publishes a report page with the name for every check.
    /// </summary>
    public bool OutsideCheckAllowed { get; set; }
}

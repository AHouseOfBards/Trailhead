using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Trailhead.Configuration;

/// <summary>Trailhead's saved settings. Empty until the setup flow exists.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The web address the certificate is for (a Dynu name). Empty until setup has claimed one.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>The Dynu API key, sealed (a <c>cg1:</c> string). Never plain text; see Dns/DynuKey.cs.</summary>
    public string DynuKey { get; set; } = string.Empty;
}

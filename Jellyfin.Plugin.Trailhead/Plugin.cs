using Jellyfin.Plugin.Trailhead.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// Trailhead: remote access for Jellyfin in one guided setup.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>Never changes: Jellyfin and the catalog match installs and updates on it.</summary>
    internal const string IdText = "e88e0913-0a07-4904-bcdc-80f925458cac";

    private readonly IXmlSerializer _xml;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _xml = xmlSerializer;
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Trailhead";

    public override Guid Id => Guid.Parse(IdText);

    public override string Description => "Remote access for Jellyfin: a web address, a certificate, the router and Jellyfin's own settings, in one guided setup.";

    /// <summary>
    /// Jellyfin's generic save (<c>POST /Plugins/{id}/Configuration</c>) lands here. It copies the posted values onto
    /// the configuration already in use, and leaves the Trailhead-owned settings (the address, the sealed key and the
    /// name records) as they are: they change only through Trailhead's own routes, so there is one way to set each, and
    /// a plain-text key posted here can't land in the saved file.
    /// </summary>
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var posted = (PluginConfiguration)configuration;
        ConfigGate.Locked(() =>
        {
            var inUse = Configuration;
            ConfigGate.CopyValues(posted, inUse, includeOwned: false);
            base.UpdateConfiguration(inUse);
        });
    }

    /// <summary>The configuration as it is in the saved file, not as it is in memory.</summary>
    internal PluginConfiguration ReadSavedConfiguration()
        => (PluginConfiguration)_xml.DeserializeFromFile(typeof(PluginConfiguration), ConfigurationFilePath);
}

using Jellyfin.Plugin.Trailhead.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Trailhead;

/// <summary>
/// Trailhead: remote access for Jellyfin in one guided setup.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>Never changes: Jellyfin and the catalog match installs and updates on it.</summary>
    internal const string IdText = "e88e0913-0a07-4904-bcdc-80f925458cac";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Trailhead";

    public override Guid Id => Guid.Parse(IdText);

    public override string Description => "Remote access for Jellyfin: a web address, a certificate, the router and Jellyfin's own settings, in one guided setup.";
}

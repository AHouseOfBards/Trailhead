using System.Text.RegularExpressions;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

/// <summary>
/// The setup page is registered the way Jellyfin 12 wants it: <c>IHasWebPages.GetPages()</c>, and the page served
/// by name from the plugin assembly (DashboardController.GetDashboardConfigurationPage at tag v12.0).
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class PagesTests : ApiTestBase
{
    private IReadOnlyList<PluginPageInfo> Pages()
    {
        var hasPages = Assert.IsAssignableFrom<IHasWebPages>(ThePlugin);
        var pages = hasPages.GetPages().ToList();
        Assert.NotEmpty(pages);
        return pages;
    }

    private PluginPageInfo Find(string name)
        => Pages().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // The same lookup DashboardController does: by Name, ignoring case, then the stream by EmbeddedResourcePath.
    private Stream? Serve(string name)
    {
        var page = Pages().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        return page is null ? null : ThePlugin.GetType().Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
    }

    private string Text(string name)
    {
        using var stream = Serve(name) ?? throw new InvalidOperationException("not served: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_plugin_registers_the_page_and_its_script()
    {
        var names = Pages().Select(p => p.Name).ToList();

        Assert.Contains("Trailhead", names);
        Assert.Contains("trailhead.js", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_registered_page_resolves_to_an_embedded_resource_with_content()
    {
        foreach (var page in Pages())
        {
            using var stream = ThePlugin.GetType().Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            Assert.True(stream is not null, "missing embedded resource " + page.EmbeddedResourcePath);
            Assert.True(stream!.Length > 100, page.EmbeddedResourcePath + " is empty");
        }
    }

    [Fact]
    public void Both_files_are_served_by_name_and_the_type_comes_from_the_extension()
    {
        Assert.NotNull(Serve("Trailhead"));
        Assert.NotNull(Serve("trailhead.js"));
        Assert.NotNull(Serve("TRAILHEAD.JS"));
        Assert.Null(Serve("nothing-here"));

        Assert.StartsWith("text/html", MimeTypes.GetMimeType(Find("Trailhead").EmbeddedResourcePath));
        // jellyfin-web imports the script as a module, and a browser refuses a module that isn't a JavaScript type.
        Assert.Contains("javascript", MimeTypes.GetMimeType(Find("trailhead.js").EmbeddedResourcePath));
    }

    [Fact]
    public void Trailhead_is_in_the_main_menu_with_the_hiking_icon_and_the_script_is_not()
    {
        var page = Find("Trailhead");
        Assert.True(page.EnableInMainMenu);
        Assert.Equal("hiking", page.MenuIcon);
        Assert.Equal("Trailhead", page.DisplayName);

        Assert.False(Find("trailhead.js").EnableInMainMenu);
    }

    [Fact]
    public void The_page_names_the_script_by_the_name_the_plugin_registers()
    {
        var html = Text("Trailhead");

        var match = Regex.Match(html, "data-role=\"page\"[^>]*data-controller=\"__plugin/([^\"]+)\"");
        Assert.True(match.Success, "the page element has no data-controller=\"__plugin/...\"");
        // jellyfin-web fetches configurationpage?name=<rest>, so the rest has to be a registered page.
        Assert.NotNull(Serve(match.Groups[1].Value));
    }

    [Fact]
    public void The_page_holds_no_state_because_anyone_can_fetch_it()
    {
        // GetDashboardConfigurationPage has no [Authorize]. Nothing here may be a key, a name or an address.
        foreach (var file in new[] { "Trailhead", "trailhead.js" })
        {
            var text = Text(file);
            Assert.DoesNotContain(Sentinel, text);
            Assert.DoesNotContain("cg1:", text);
        }
    }
}

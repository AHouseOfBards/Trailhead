using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Renewal;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Setup;

[Collection(RegistryCollection.Name)]
public sealed class ConfigGateTests : ApiTestBase
{
    [Fact]
    public void Update_saves_and_gives_back_what_the_file_says()
    {
        var saved = ConfigGate.Update(c => c.Hostname = "one.freeddns.org");

        Assert.Equal("one.freeddns.org", saved.Hostname);
        Assert.NotSame(ThePlugin.Configuration, saved);
        Assert.Contains("one.freeddns.org", SavedXml);
    }

    [Fact]
    public void A_save_after_Jellyfin_swaps_the_configuration_object_is_not_lost()
    {
        ConfigGate.Update(c => c.Hostname = "one.freeddns.org");

        // What Jellyfin's own save does: a new object, now the one in use.
        RenewalTestKit.SwapConfiguration(ThePlugin, new PluginConfiguration { Hostname = "one.freeddns.org" });
        ConfigGate.Update(c => c.NameCreatedByTrailhead = "one.freeddns.org");

        var onDisk = ThePlugin.ReadSavedConfiguration();
        Assert.Equal("one.freeddns.org", onDisk.NameCreatedByTrailhead);
        Assert.Equal("one.freeddns.org", ThePlugin.Configuration.NameCreatedByTrailhead);
    }

    [Fact]
    public async Task Parallel_updates_lose_none()
    {
        const int Writers = 24;

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(_ => Task.Run(() => ConfigGate.Update(c =>
        {
            // Read, wait, write: without one lock around all three, two writers read the same value.
            var seen = c.Hostname;
            Thread.Sleep(2);
            c.Hostname = seen + "x";
        }))));

        Assert.Equal(new string('x', Writers), ThePlugin.ReadSavedConfiguration().Hostname);
    }

    [Fact]
    public void The_generic_save_keeps_the_owned_settings()
    {
        SeedKey();
        ConfigGate.Update(c =>
        {
            c.Hostname = "good.freeddns.org";
            c.NameCreatedByTrailhead = "good.freeddns.org";
        });
        var sealedBefore = Config.DynuKey;
        var inUse = ThePlugin.Configuration;
        var changed = 0;
        ThePlugin.ConfigurationChanged += (_, _) => changed++;

        ThePlugin.UpdateConfiguration(new PluginConfiguration
        {
            Hostname = "evil.example.com",
            DynuKey = "PLAIN-TEXT-KEY-POSTED",
            PendingName = "other.example.com",
            NameCreatedByTrailhead = "mine.example.com",
        });

        Assert.Same(inUse, ThePlugin.Configuration);
        Assert.Equal("good.freeddns.org", Config.Hostname);
        Assert.Equal(sealedBefore, Config.DynuKey);
        Assert.Equal(string.Empty, Config.PendingName);
        Assert.Equal("good.freeddns.org", Config.NameCreatedByTrailhead);
        Assert.DoesNotContain("PLAIN-TEXT-KEY-POSTED", SavedXml);
        Assert.DoesNotContain("evil.example.com", SavedXml);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(FlakyXml.Mode.WritesBlank)]
    [InlineData(FlakyXml.Mode.ReadFails)]
    [InlineData(FlakyXml.Mode.WriteFails)]
    public void A_write_that_does_not_read_back_is_an_error_and_memory_goes_back(FlakyXml.Mode mode)
    {
        ConfigGate.Update(c => c.Hostname = "kept.freeddns.org");
        Xml.Current = mode;

        var error = Assert.Throws<ConfigSaveException>(() => ConfigGate.Update(c => c.Hostname = "lost.freeddns.org"));

        Assert.Equal("kept.freeddns.org", ThePlugin.Configuration.Hostname);
        Assert.DoesNotContain("freeddns", error.Message);
    }

    [Fact]
    public void With_no_plugin_there_is_nothing_to_save_to()
    {
        RenewalTestKit.ForgetPlugin();

        Assert.Throws<ConfigSaveException>(() => ConfigGate.Update(c => c.Hostname = "a.freeddns.org"));
    }
}

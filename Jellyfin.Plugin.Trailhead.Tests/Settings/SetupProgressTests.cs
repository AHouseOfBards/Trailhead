using Jellyfin.Plugin.Trailhead.Configuration;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

public sealed class SetupProgressTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string File => _folder.File("setup.json");

    private SetupProgress Make(PluginConfiguration? config = null) => new(() => File, () => config ?? new PluginConfiguration());

    [Fact]
    public void A_new_install_starts_at_the_key_with_no_safe_point_and_writes_nothing()
    {
        var position = Make().Read();

        Assert.Equal(SetupStep.Key, position.Next);
        Assert.Null(position.LastSafe);
        Assert.Empty(position.Done);
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void An_install_that_saved_a_key_and_a_name_before_this_record_existed_continues_after_them()
    {
        var config = new PluginConfiguration { DynuKey = "cg1:sealed", Hostname = "trailhead-test.example.com" };

        var position = Make(config).Read();

        Assert.Equal(SetupStep.AdminWarning, position.Next);
        Assert.Equal(SetupStep.Name, position.LastSafe);
    }

    [Fact]
    public void A_key_alone_continues_at_the_name()
    {
        Assert.Equal(SetupStep.Name, Make(new PluginConfiguration { DynuKey = "cg1:sealed" }).Read().Next);
    }

    [Fact]
    public void Steps_done_survive_a_new_copy_and_marking_twice_changes_nothing()
    {
        var config = new PluginConfiguration { DynuKey = "cg1:sealed", Hostname = "trailhead-test.example.com" };
        var progress = Make(config);
        progress.MarkDone(SetupStep.AdminWarning);
        progress.MarkDone(SetupStep.Certificate);
        var written = System.IO.File.ReadAllText(File);

        progress.MarkDone(SetupStep.Certificate);
        var later = Make(config).Read();

        Assert.Equal(written, System.IO.File.ReadAllText(File));
        Assert.Equal(SetupStep.JellyfinSettings, later.Next);
        Assert.Equal(SetupStep.Certificate, later.LastSafe);
    }

    [Fact]
    public void A_gap_is_where_setup_continues_even_with_later_steps_marked()
    {
        var progress = Make(new PluginConfiguration { DynuKey = "cg1:sealed", Hostname = "trailhead-test.example.com" });
        progress.MarkDone(SetupStep.AdminWarning);
        progress.MarkDone(SetupStep.JellyfinSettings);

        Assert.Equal(SetupStep.Certificate, progress.Read().Next);
    }

    [Fact]
    public void The_restart_counts_as_done_only_once_a_new_start_reads_the_record()
    {
        var config = new PluginConfiguration { DynuKey = "cg1:sealed", Hostname = "trailhead-test.example.com" };
        var before = Make(config);
        foreach (var s in new[] { SetupStep.AdminWarning, SetupStep.Certificate, SetupStep.JellyfinSettings })
        {
            before.MarkDone(s);
        }

        before.RestartAsked();
        var sameStart = before.Read();
        var nextStart = Make(config).Read();

        Assert.Equal(SetupStep.Restart, sameStart.Next);
        Assert.Equal(SetupStep.Gate, nextStart.Next);
    }

    [Fact]
    public void Answers_are_kept_so_they_are_not_asked_again()
    {
        Make().Answer(a =>
        {
            a.DockerSubnet = "192.168.1.0/24";
            a.FinishRouterLater = true;
        });

        var answers = Make().Read().Answers;

        Assert.Equal("192.168.1.0/24", answers.DockerSubnet);
        Assert.True(answers.FinishRouterLater);
    }

    [Fact]
    public void A_step_name_from_a_newer_version_is_ignored_and_kept()
    {
        System.IO.File.WriteAllText(File, "{\"done\":[\"Key\",\"Teleport\"],\"answers\":{}}");

        var position = Make().Read();
        var after = Make().MarkDone(SetupStep.Name);

        Assert.Equal(SetupStep.Name, position.Next);
        Assert.Equal(SetupStep.AdminWarning, after.Next);
        Assert.Contains("Teleport", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void An_unreadable_record_starts_over_from_what_the_configuration_says()
    {
        System.IO.File.WriteAllText(File, "nonsense");

        Assert.Equal(SetupStep.Name, Make(new PluginConfiguration { DynuKey = "cg1:sealed" }).Read().Next);
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        var progress = Make();
        progress.MarkDone(SetupStep.Key);

        progress.Clear();

        Assert.Equal(SetupStep.Key, Make().Read().Next);
        Assert.False(System.IO.File.Exists(File));
    }
}

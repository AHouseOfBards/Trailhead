using System.Text.Json;
using Jellyfin.Plugin.Trailhead.Settings;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Settings;

public sealed class JournalTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ListLogger _logger = new();

    public void Dispose() => _folder.Dispose();

    private string JournalFile => _folder.File("journal.json");

    private Journal Make(TimeProvider? time = null) => new(() => JournalFile, time ?? TimeProvider.System, _logger);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task The_entry_is_on_disk_with_the_old_value_before_Jellyfin_is_asked_to_save()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        JournalEntry? seenAtSave = null;
        rig.Config.NamedConfigurationUpdating += (_, _) =>
            seenAtSave = new Journal(() => Path.Combine(rig.Folder, "journal.json"), TimeProvider.System, _logger).Find(JournalKinds.JellyfinSetting, NetworkKeys.KnownProxies);

        await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);

        Assert.NotNull(seenAtSave);
        Assert.Equal(new[] { "10.0.0.5" }, seenAtSave!.Before!.Value.Deserialize<string[]>());
        Assert.Equal(new[] { "10.0.0.5", "127.0.0.1" }, seenAtSave.Written!.Value.Deserialize<string[]>());
    }

    [Fact]
    public async Task A_save_that_lands_and_then_throws_leaves_an_entry_whose_before_puts_it_back_after_a_restart()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        rig.Config.FailNextSaveAfterWrite = new IOException("power cut");

        var result = await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);
        rig.Config.Restart();
        Assert.Equal(new[] { "10.0.0.5", "127.0.0.1" }, rig.Config.ReadFile().KnownProxies);

        var restored = await rig.Network.RestoreAsync(null, false, CancellationToken.None);

        Assert.Equal(SettingOutcome.Failed, result.Outcome);
        Assert.Equal(RestoreOutcome.Restored, Assert.Single(restored).Outcome);
        Assert.Equal(new[] { "10.0.0.5" }, rig.Config.ReadFile().KnownProxies);
        Assert.Empty(rig.Journal.Entries());
    }

    [Fact]
    public async Task A_save_that_throws_before_writing_leaves_an_entry_that_restore_finds_already_back()
    {
        using var rig = new NetworkRig(new NetworkConfiguration { KnownProxies = new[] { "10.0.0.5" } });
        rig.Config.FailNextSave = new IOException("disk full");

        var result = await rig.Network.ApplyOneAsync(NetworkKeys.KnownProxies, NetworkRig.Plan(), CancellationToken.None);
        var entry = Assert.Single(rig.Journal.Entries());
        var restored = await rig.Network.RestoreAsync(null, false, CancellationToken.None);

        Assert.Equal(SettingProblems.SaveFailed, result.Problem);
        Assert.Equal(new[] { "10.0.0.5" }, entry.Before!.Value.Deserialize<string[]>());
        Assert.Equal(RestoreOutcome.AlreadyBefore, Assert.Single(restored).Outcome);
        Assert.Empty(rig.Journal.Entries());
    }

    [Fact]
    public void Recording_a_key_again_keeps_the_first_before_and_the_same_id()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
        var journal = Make(time);

        var first = journal.Record(JournalKinds.JellyfinSetting, "network.KnownProxies", Json(new[] { "admin" }), Json(new[] { "admin", "a" }));
        time.Advance(TimeSpan.FromHours(1));
        var second = journal.Record(JournalKinds.JellyfinSetting, "network.KnownProxies", Json(new[] { "admin", "a" }), Json(new[] { "admin", "b" }));
        var saved = Assert.Single(Make().Entries());

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(new[] { "admin" }, saved.Before!.Value.Deserialize<string[]>());
        Assert.Equal(new[] { "admin", "b" }, saved.Written!.Value.Deserialize<string[]>());
        Assert.Equal(first.FirstAt, saved.FirstAt);
        Assert.Equal(first.FirstAt.AddHours(1), saved.At);
    }

    [Fact]
    public void A_kind_this_version_does_not_know_is_kept()
    {
        var journal = Make();
        journal.Record("SomethingNewer", "x", Json(1), Json(2));
        journal.Record(JournalKinds.JellyfinSetting, "y", Json(1), Json(2));

        Assert.Equal(new[] { "SomethingNewer", JournalKinds.JellyfinSetting }, Make().Entries().Select(e => e.Kind));
    }

    [Fact]
    public void An_unreadable_journal_is_kept_aside_reported_and_blocks_new_changes_until_seen()
    {
        File.WriteAllText(JournalFile, "{ \"entries\": [ {\"id\": ");
        var journal = Make();

        var damaged = journal.DamagedFile;
        var refused = Assert.Throws<JournalUnavailableException>(() => journal.Record(JournalKinds.JellyfinSetting, "k", Json(1), Json(2)));

        Assert.NotNull(damaged);
        Assert.StartsWith("journal.bad-", damaged);
        Assert.Equal("{ \"entries\": [ {\"id\": ", File.ReadAllText(_folder.File(damaged!)));
        Assert.Contains(damaged!, _logger.All);
        Assert.NotNull(refused);

        // A restart doesn't forget it: the note is in the journal itself.
        Assert.Equal(damaged, Make().DamagedFile);

        journal.AcknowledgeDamage();
        journal.Record(JournalKinds.JellyfinSetting, "k", Json(1), Json(2));
        Assert.Null(Make().DamagedFile);
        Assert.Single(Make().Entries());
        Assert.True(File.Exists(_folder.File(damaged!)));
    }

    [Fact]
    public void A_bad_journal_with_a_good_backup_recovers_the_backup_and_still_reports_the_damage()
    {
        var journal = Make();
        journal.Record(JournalKinds.JellyfinSetting, "first", Json(1), Json(2));
        journal.Record(JournalKinds.JellyfinSetting, "second", Json(3), Json(4));
        File.WriteAllText(JournalFile, "not json");

        var entries = Make().Entries();

        Assert.Equal(new[] { "first" }, entries.Select(e => e.Key));
        Assert.NotNull(Make().DamagedFile);
    }

    [Fact]
    public void Entries_that_parse_but_lack_an_id_count_as_unreadable()
    {
        File.WriteAllText(JournalFile, "{\"entries\":[{\"kind\":\"JellyfinSetting\",\"key\":\"k\"}]}");

        Assert.NotNull(Make().DamagedFile);
    }

    [Fact]
    public async Task Concurrent_writers_on_two_instances_lose_nothing()
    {
        var a = Make();
        var b = Make();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            (i % 2 == 0 ? a : b).Record(JournalKinds.JellyfinSetting, "key" + i, Json(i), Json(i + 1)))));

        var keys = Make().Entries().Select(e => e.Key).Order().ToList();
        Assert.Equal(Enumerable.Range(0, 40).Select(i => "key" + i).Order().ToList(), keys);
    }

    [Fact]
    public void Nothing_is_written_until_a_change_is_recorded()
    {
        var journal = Make();

        Assert.Empty(journal.Entries());
        Assert.Null(journal.DamagedFile);
        Assert.False(File.Exists(JournalFile));
    }

    [Fact]
    public void Given_nothing_to_record_it_refuses()
    {
        var journal = Make();

        Assert.ThrowsAny<ArgumentException>(() => journal.Record(string.Empty, string.Empty, null, null));
        Assert.False(File.Exists(JournalFile));
    }

    [Fact]
    public void Remove_drops_only_the_named_entries()
    {
        var journal = Make();
        var keep = journal.Record(JournalKinds.JellyfinSetting, "keep", Json(1), Json(2));
        var drop = journal.Record(JournalKinds.JellyfinSetting, "drop", Json(1), Json(2));

        journal.Remove(new[] { drop.Id, "no-such-id" });

        Assert.Equal(new[] { keep.Id }, Make().Entries().Select(e => e.Id));
    }
}

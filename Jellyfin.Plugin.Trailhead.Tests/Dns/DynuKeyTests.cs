using Campground.Shared.Security;
using Jellyfin.Plugin.Trailhead.Dns;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

public sealed class DynuKeyTests
{
    private const string Plain = "FAKE-DYNU-KEY-do-not-use-0000";

    private static SecretBox Box(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    [Fact]
    public void A_key_round_trips_and_is_stored_sealed()
    {
        var key = new DynuKey(Box(1));

        var saved = key.Seal(Plain);

        Assert.StartsWith("cg1:", saved);
        Assert.DoesNotContain(Plain, saved);
        Assert.True(key.TryOpen(saved, out var opened));
        Assert.Equal(Plain, opened);
    }

    [Fact]
    public void A_value_sealed_for_another_purpose_asks_for_the_key_again()
    {
        var other = Box(1).Seal(Plain, "some:other:place");

        Assert.False(new DynuKey(Box(1)).TryOpen(other, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void A_value_sealed_with_no_associated_data_does_not_open_either_and_ours_does_not_open_without_it()
    {
        // Sealed by the same key but by something that didn't bind it to this setting.
        Assert.False(new DynuKey(Box(1)).TryOpen(Box(1).Seal(Plain), out _));

        // And the other way round: ours is bound, so a plain open of it fails.
        var ours = new DynuKey(Box(1)).Seal(Plain);
        Assert.Null(Box(1).TryOpen(ours));
        Assert.Equal(Plain, Box(1).TryOpen(ours, "trailhead:dynu"));
    }

    [Fact]
    public void A_value_from_another_machines_key_file_asks_for_the_key_again()
    {
        var saved = new DynuKey(Box(1)).Seal(Plain);

        Assert.False(new DynuKey(Box(2)).TryOpen(saved, out var key));
        Assert.Null(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cg1:")]
    [InlineData("cg1:not base64 !!")]
    [InlineData(Plain)]
    public void Nothing_usable_asks_for_the_key_again_and_does_not_throw(string? saved)
    {
        // A plain-text value counts as unusable too: the key must only ever be read from a sealed one.
        Assert.False(new DynuKey(Box(1)).TryOpen(saved, out var key));
        Assert.Null(key);
        Assert.Contains("Re-enter", DynuKey.ReenterMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_empty_key_cannot_be_sealed(string plain)
    {
        Assert.Throws<ArgumentException>(() => new DynuKey(Box(1)).Seal(plain));
    }

    [Fact]
    public void Whitespace_around_a_pasted_key_is_not_part_of_it()
    {
        var key = new DynuKey(Box(1));

        Assert.True(key.TryOpen(key.Seal("  " + Plain + "\n"), out var opened));
        Assert.Equal(Plain, opened);
    }
}

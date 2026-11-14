using System.Security.Cryptography;
using Campground.Shared.Security;
using Certes;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class AccountStoreTests : IDisposable
{
    private const string AssociatedData = "trailhead:acme-account";
    private readonly TempFolder _folder = new();
    private readonly ListLogger _log = new();
    private readonly SecretBox _box = new(RandomNumberGenerator.GetBytes(32));

    public void Dispose() => _folder.Dispose();

    private AccountStore Store() => new(_folder.File("account.key"), _box, _log);

    [Fact]
    public void A_key_round_trips()
    {
        var first = Store().LoadOrCreate();

        var second = Store().LoadOrCreate();

        Assert.Equal(first.ToPem(), second.ToPem());
        Assert.Equal(KeyAlgorithm.ES256, second.Algorithm);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void The_file_is_sealed_and_holds_no_PEM()
    {
        var key = Store().LoadOrCreate();

        var onDisk = File.ReadAllText(_folder.File("account.key"));

        Assert.StartsWith(SecretBox.Prefix, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", onDisk);
        Assert.Equal(key.ToPem(), _box.Open(onDisk, AssociatedData));
    }

    [Fact]
    public void A_file_sealed_by_another_key_means_a_new_account_logged_once()
    {
        var foreign = new SecretBox(RandomNumberGenerator.GetBytes(32));
        var old = KeyFactory.NewKey(KeyAlgorithm.ES256);
        File.WriteAllText(_folder.File("account.key"), foreign.Seal(old.ToPem(), AssociatedData));

        var fresh = Store().LoadOrCreate();
        var again = Store().LoadOrCreate();

        Assert.NotEqual(old.ToPem(), fresh.ToPem());
        Assert.Equal(fresh.ToPem(), again.ToPem());
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void A_file_sealed_for_another_purpose_is_not_taken_as_the_account()
    {
        var old = KeyFactory.NewKey(KeyAlgorithm.ES256);
        File.WriteAllText(_folder.File("account.key"), _box.Seal(old.ToPem(), "trailhead:something-else"));

        var fresh = Store().LoadOrCreate();

        Assert.NotEqual(old.ToPem(), fresh.ToPem());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a sealed value")]
    public void Empty_or_garbage_input_means_a_new_account_not_a_failure(string content)
    {
        File.WriteAllText(_folder.File("account.key"), content);

        var key = Store().LoadOrCreate();

        Assert.Equal(key.ToPem(), Store().LoadOrCreate().ToPem());
    }

    [Fact]
    public void Sealed_text_that_is_not_a_key_is_a_new_account()
    {
        File.WriteAllText(_folder.File("account.key"), _box.Seal("hello", AssociatedData));

        var key = Store().LoadOrCreate();

        Assert.Equal(KeyAlgorithm.ES256, key.Algorithm);
    }

    [Fact]
    public void Nothing_secret_is_logged()
    {
        var foreign = new SecretBox(RandomNumberGenerator.GetBytes(32));
        var old = KeyFactory.NewKey(KeyAlgorithm.ES256);
        File.WriteAllText(_folder.File("account.key"), foreign.Seal(old.ToPem(), AssociatedData));

        var key = Store().LoadOrCreate();

        Assert.DoesNotContain("PRIVATE KEY", _log.All);
        Assert.DoesNotContain(old.ToPem().Split('\n')[1], _log.All);
        Assert.DoesNotContain(key.ToPem().Split('\n')[1], _log.All);
    }
}

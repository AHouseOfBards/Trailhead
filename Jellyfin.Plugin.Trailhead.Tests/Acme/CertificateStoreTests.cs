using System.Security.Cryptography;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class CertificateStoreTests : IDisposable
{
    private const string Name = "home.example.org";
    private const string Root = "Test Root X1";
    private readonly TempFolder _folder = new();
    private readonly ListLogger _log = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _folder.Dispose();

    private CertificateStore Store() => new(_folder.Path, _time, _log);

    private static CertificateExpectation Expect(string name = Name, string root = Root) => new(name, root);

    private (string Pem, RSA Key) Make(string name = Name, string root = Root, TimeSpan? validFor = null)
    {
        var now = _time.GetUtcNow();
        var (pem, key, _) = TestPki.Issue(name, root, now.AddHours(-1), now + (validFor ?? TimeSpan.FromDays(90)));
        return (pem, key);
    }

    private string[] Leftovers() => Directory.GetFiles(_folder.Path).Select(Path.GetFileName).Where(f => f != "current.pfx" && f != "previous.pfx").ToArray()!;

    [Fact]
    public void An_empty_folder_has_no_certificate()
    {
        Assert.Null(Store().Load());
        Assert.Null(Store().LoadPrevious());
    }

    [Fact]
    public void Installs_a_good_certificate_and_loads_it_with_its_key_and_chain()
    {
        var (pem, key) = Make();

        var result = Store().Install(pem, key, Expect());

        Assert.True(result.Ok, result.Reason);
        var loaded = Assert.IsType<StoredCertificate>(Store().Load());
        Assert.True(loaded.Leaf.HasPrivateKey);
        Assert.Contains(Name, loaded.Leaf.Subject);
        Assert.Equal(2, loaded.Chain.Count);
        Assert.Equal(loaded.Leaf.Thumbprint, loaded.Chain[0].Thumbprint);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Replacing_keeps_the_previous_certificate_and_leaves_no_temp_file()
    {
        var (pem1, key1) = Make();
        Assert.True(Store().Install(pem1, key1, Expect()).Ok);
        var first = Store().Load()!.Leaf.Thumbprint;
        _time.Advance(TimeSpan.FromDays(60));
        var (pem2, key2) = Make();

        Assert.True(Store().Install(pem2, key2, Expect()).Ok);

        Assert.NotEqual(first, Store().Load()!.Leaf.Thumbprint);
        Assert.Equal(first, Store().LoadPrevious()!.Leaf.Thumbprint);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void A_failed_check_leaves_the_current_certificate_in_place()
    {
        var (pem1, key1) = Make();
        Assert.True(Store().Install(pem1, key1, Expect()).Ok);
        var before = File.ReadAllBytes(_folder.File("current.pfx"));
        var (pem2, key2) = Make("other.example.org");

        var result = Store().Install(pem2, key2, Expect());

        Assert.False(result.Ok);
        Assert.Equal(before, File.ReadAllBytes(_folder.File("current.pfx")));
        Assert.False(File.Exists(_folder.File("previous.pfx")));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public void Refuses_a_certificate_for_the_wrong_name()
    {
        var (pem, key) = Make("other.example.org");

        var result = Store().Install(pem, key, Expect());

        Assert.False(result.Ok);
        Assert.Contains("name", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Refuses_a_chain_that_does_not_lead_to_the_expected_root()
    {
        var (pem, key) = Make(root: "Some Other Root");

        var result = Store().Install(pem, key, Expect());

        Assert.False(result.Ok);
        Assert.Contains("root", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Refuses_an_expired_certificate()
    {
        var (pem, key) = Make(validFor: TimeSpan.FromDays(10));
        _time.Advance(TimeSpan.FromDays(11));

        var result = Store().Install(pem, key, Expect());

        Assert.False(result.Ok);
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Refuses_a_certificate_with_less_than_a_day_left()
    {
        var (pem, key) = Make(validFor: TimeSpan.FromHours(12));

        var result = Store().Install(pem, key, Expect());

        Assert.False(result.Ok);
        Assert.Contains("day", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_a_certificate_with_just_over_a_day_left()
    {
        var (pem, key) = Make(validFor: TimeSpan.FromHours(25));

        Assert.True(Store().Install(pem, key, Expect()).Ok);
    }

    [Fact]
    public void Refuses_a_key_that_does_not_match()
    {
        var (pem, _) = Make();
        using var wrong = RSA.Create(2048);

        var result = Store().Install(pem, wrong, Expect());

        Assert.False(result.Ok);
        Assert.Contains("key", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a certificate")]
    public void Refuses_input_that_is_not_a_chain_without_throwing(string pem)
    {
        using var key = RSA.Create(2048);

        var result = Store().Install(pem, key, Expect());

        Assert.False(result.Ok);
    }

    [Fact]
    public void A_corrupt_current_file_loads_as_no_certificate()
    {
        File.WriteAllBytes(_folder.File("current.pfx"), new byte[] { 1, 2, 3 });

        Assert.Null(Store().Load());
    }

    [Fact]
    public void Nothing_secret_is_logged()
    {
        var (pem, key) = Make("other.example.org");

        Store().Install(pem, key, Expect());

        Assert.DoesNotContain("PRIVATE", _log.All);
        Assert.DoesNotContain(Convert.ToBase64String(key.ExportPkcs8PrivateKey())[..40], _log.All);
    }
}

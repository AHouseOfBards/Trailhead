using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Dns;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Renewal;

/// <summary>
/// The one manager per lifetime, built from the saved settings at the moment of use. Every test here has no network:
/// either the issuer is a fake, or nothing gets as far as asking it.
/// </summary>
public sealed class CertificateServicesTests : IDisposable
{
    private const string Host = "trailhead-test.example.com";

    private readonly TempFolder _folder = new();
    private readonly ListLogger _log = new();
    private readonly MutableSettings _settings = new();

    public void Dispose() => _folder.Dispose();

    private static ReconcileResult Canned(ReconcileOutcome outcome) => new(outcome, null, null, null);

    private ServicesOverrides Fakes(Func<ICertificateIssuer>? issuer = null, Func<ICertificateRenewer>? renewer = null) => new()
    {
        Box = RenewalTestKit.Box(1),
        DataFolder = _folder.File("data"),
        IssuerFactory = issuer ?? (() => throw new InvalidOperationException("nothing here may reach the CA")),
        RenewerFactory = renewer,
    };

    private void SetUp(string host = Host, string plainKey = FakeDynu.Key, byte boxSeed = 1)
        => _settings.Value = new ServiceSettings(host, RenewalTestKit.Sealed(boxSeed, plainKey));

    [Fact]
    public async Task An_empty_hostname_is_not_set_up_and_touches_nothing()
    {
        _settings.Value = new ServiceSettings(string.Empty, RenewalTestKit.Sealed(1, FakeDynu.Key));
        using var services = RenewalTestKit.Services(_log, _settings, Fakes());

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.NotSetUp, report.State);
        Assert.Equal(0, services.Builds);
        Assert.False(Directory.Exists(_folder.File("data")), "it made its folders for a server that isn't set up");
    }

    [Fact]
    public async Task A_hostname_that_is_not_a_host_name_is_not_set_up()
    {
        SetUp(host: "https://not a host/");
        using var services = RenewalTestKit.Services(_log, _settings, Fakes());

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.NotSetUp, report.State);
        Assert.Equal(0, services.Builds);
    }

    [Fact]
    public async Task An_empty_key_is_not_set_up()
    {
        _settings.Value = new ServiceSettings(Host, string.Empty);
        using var services = RenewalTestKit.Services(_log, _settings, Fakes());

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.NotSetUp, report.State);
        Assert.Equal(0, services.Builds);
    }

    [Fact]
    public async Task A_key_sealed_somewhere_else_is_not_set_up_and_asks_for_the_key_again()
    {
        SetUp(boxSeed: 2);
        using var services = RenewalTestKit.Services(_log, _settings, Fakes());

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.NotSetUp, report.State);
        Assert.Contains(DynuKey.ReenterMessage, report.Message);
        Assert.Equal(0, services.Builds);
    }

    [Fact]
    public async Task The_same_settings_reuse_the_manager_and_changed_settings_build_another()
    {
        SetUp();
        var made = 0;
        using var services = RenewalTestKit.Services(_log, _settings, Fakes(renewer: () =>
        {
            made++;
            return new CannedRenewer(Canned(ReconcileOutcome.UpToDate));
        }));

        await services.RenewAsync(CancellationToken.None);
        await services.RenewAsync(CancellationToken.None);
        Assert.Equal(1, made);

        // Settings are compared by value, not by the object: Jellyfin swaps in a new configuration object on every save.
        _settings.Value = new ServiceSettings(_settings.Value.Hostname, _settings.Value.SealedDynuKey);
        await services.RenewAsync(CancellationToken.None);
        Assert.Equal(1, made);

        SetUp(host: "other.example.com");
        await services.RenewAsync(CancellationToken.None);
        Assert.Equal(2, made);

        SetUp(host: "other.example.com", plainKey: "A-DIFFERENT-FAKE-KEY");
        await services.RenewAsync(CancellationToken.None);
        Assert.Equal(3, made);
        Assert.Equal(3, services.Builds);
    }

    [Fact]
    public async Task A_manager_built_for_the_old_name_is_not_used_after_the_name_changes()
    {
        SetUp();
        var names = new List<string>();
        using var services = RenewalTestKit.Services(_log, _settings, Fakes(renewer: () =>
        {
            names.Add(_settings.Value.Hostname);
            return new CannedRenewer(Canned(ReconcileOutcome.Issued));
        }));
        await services.RenewAsync(CancellationToken.None);

        SetUp(host: "second.example.com");
        await services.RenewAsync(CancellationToken.None);

        Assert.Equal(new[] { Host, "second.example.com" }, names);
    }

    [Fact]
    public void Built_from_settings_it_uses_Lets_Encrypt_staging_and_never_production()
    {
        SetUp();
        var overrides = new ServicesOverrides { Box = RenewalTestKit.Box(1), DataFolder = _folder.File("data") };
        using var services = RenewalTestKit.Services(_log, _settings, overrides);

        using var lease = services.Acquire(out var report);

        Assert.True(lease is not null, report?.Message);
        Assert.Equal(AcmeEndpoints.Staging, services.ActiveDirectory);
        Assert.NotEqual(AcmeEndpoints.Production, services.ActiveDirectory);
    }

    [Fact]
    public async Task An_exception_while_building_is_caught_and_the_next_run_tries_again()
    {
        SetUp();

        // A file where the data folder should be: the certificate store can't make its folder.
        File.WriteAllText(_folder.File("data"), "in the way");
        using var services = RenewalTestKit.Services(_log, _settings, new ServicesOverrides { Box = RenewalTestKit.Box(1), DataFolder = _folder.File("data") });

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.BuildFailed, report.State);
        Assert.Equal(0, services.Builds);

        File.Delete(_folder.File("data"));
        using var lease = services.Acquire(out var again);
        Assert.True(lease is not null, again?.Message);
    }

    [Fact]
    public async Task Two_runs_at_once_share_one_issuance()
    {
        SetUp();
        var issuer = new GatedIssuer();
        using var services = RenewalTestKit.Services(_log, _settings, Fakes(issuer: () => issuer));

        var first = services.RenewAsync(CancellationToken.None);
        await issuer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = services.RenewAsync(CancellationToken.None);
        issuer.Gate.SetResult();
        var reports = await Task.WhenAll(first, second);

        Assert.Equal(1, issuer.Calls);
        Assert.All(reports, r => Assert.Equal(ReconcileOutcome.Failed, r.Result!.Outcome));
        Assert.Equal(1, services.Builds);
    }

    [Fact]
    public async Task Disposing_the_services_cancels_a_run_in_flight()
    {
        SetUp();
        var issuer = new GatedIssuer();
        var services = RenewalTestKit.Services(_log, _settings, Fakes(issuer: () => issuer));
        var run = services.RenewAsync(CancellationToken.None);
        await issuer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        services.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_run_after_dispose_is_a_cancel_and_not_a_new_build()
    {
        SetUp();
        var services = RenewalTestKit.Services(_log, _settings, Fakes(renewer: () => new CannedRenewer(Canned(ReconcileOutcome.UpToDate))));
        services.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.RenewAsync(CancellationToken.None));
        Assert.Equal(0, services.Builds);
    }

    [Fact]
    public async Task A_failure_whose_text_holds_the_key_and_the_token_logs_neither()
    {
        SetUp();
        var issuer = new GatedIssuer
        {
            Failure = new HttpRequestException($"GET https://api.dynu.com/v2/dns?key={FakeDynu.Key}&token={FakeDynu.Token} failed"),
        };
        issuer.Gate.SetResult();
        using var services = RenewalTestKit.Services(_log, _settings, Fakes(issuer: () => issuer));

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(ReconcileOutcome.Failed, report.Result!.Outcome);
        Assert.NotEmpty(_log.Entries);
        Assert.DoesNotContain(FakeDynu.Key, _log.All);
        Assert.DoesNotContain(FakeDynu.Token, _log.All);
        Assert.DoesNotContain(FakeDynu.Key, report.Result.Message ?? string.Empty);
    }

    [Fact]
    public async Task A_build_failure_whose_text_holds_the_key_logs_and_reports_neither()
    {
        SetUp();
        using var services = RenewalTestKit.Services(_log, _settings, Fakes(issuer: () => throw new InvalidOperationException("bad key " + FakeDynu.Key)));

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.Equal(RenewalState.BuildFailed, report.State);
        Assert.DoesNotContain(FakeDynu.Key, report.Message);
        Assert.DoesNotContain(FakeDynu.Key, _log.All);
    }

    [Fact]
    public async Task Cancelling_a_waiting_caller_leaves_the_other_run_going()
    {
        SetUp();
        var issuer = new GatedIssuer();
        using var services = RenewalTestKit.Services(_log, _settings, Fakes(issuer: () => issuer));
        var first = services.RenewAsync(CancellationToken.None);
        await issuer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        var second = services.RenewAsync(cancel.Token);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        issuer.Gate.SetResult();
        var report = await first;

        Assert.Equal(1, issuer.Calls);
        Assert.Equal(ReconcileOutcome.Failed, report.Result!.Outcome);
    }

    [Fact]
    public async Task A_harness_given_nothing_fails_instead_of_passing()
    {
        // The settings reader returning nothing at all must read as "not set up", never as "up to date".
        using var services = RenewalTestKit.Services(_log, _settings, Fakes());

        var report = await services.RenewAsync(CancellationToken.None);

        Assert.NotEqual(RenewalState.Ran, report.State);
        Assert.Null(report.Result);
    }
}

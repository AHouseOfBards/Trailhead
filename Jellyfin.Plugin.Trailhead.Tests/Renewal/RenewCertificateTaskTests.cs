using Jellyfin.Plugin.Trailhead.Acme;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Jellyfin.Plugin.Trailhead.TestSupport;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Renewal;

/// <summary>
/// The scheduled task as Jellyfin runs it: it never throws (a red task is noise and the next run is the retry),
/// except for Jellyfin cancelling it, and says what happened in one line.
/// </summary>
public sealed class RenewCertificateTaskTests : IDisposable
{
    private const string Host = "trailhead-test.example.com";

    private readonly TempFolder _folder = new();
    private readonly ListLogger _log = new();
    private readonly MutableSettings _settings = new();

    public void Dispose() => _folder.Dispose();

    private static readonly DateTimeOffset Later = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private CertificateServices Services(Func<ICertificateIssuer>? issuer = null, Func<ICertificateRenewer>? renewer = null) => RenewalTestKit.Services(
        _log,
        _settings,
        new ServicesOverrides
        {
            Box = RenewalTestKit.Box(1),
            DataFolder = _folder.File("data"),
            IssuerFactory = issuer ?? (() => throw new InvalidOperationException("nothing here may reach the CA")),
            RenewerFactory = renewer,
        });

    private RenewCertificateTask NewTask(CertificateServices services) => new(services, new TypedLogger<RenewCertificateTask>(_log));

    private void SetUp() => _settings.Value = new ServiceSettings(Host, RenewalTestKit.Sealed(1, FakeDynu.Key));

    [Theory]
    [InlineData((int)ReconcileOutcome.UpToDate, LogLevel.Information)]
    [InlineData((int)ReconcileOutcome.Issued, LogLevel.Information)]
    [InlineData((int)ReconcileOutcome.Blocked, LogLevel.Warning)]
    [InlineData((int)ReconcileOutcome.Failed, LogLevel.Warning)]
    public async Task Each_outcome_gives_one_line_and_a_completed_task(int outcomeNumber, LogLevel level)
    {
        SetUp();
        var outcome = (ReconcileOutcome)outcomeNumber;
        var result = new ReconcileResult(outcome, Later, outcome == ReconcileOutcome.Failed ? FailureKind.Transient : null, outcome == ReconcileOutcome.Failed ? "timeout" : null);
        using var services = Services(renewer: () => new CannedRenewer(result));
        var progress = new RecordingProgress();

        await NewTask(services).ExecuteAsync(progress, CancellationToken.None);

        var line = Assert.Single(_log.Entries);
        Assert.Equal(level, line.Level);
        Assert.Equal(new double[] { 0, 100 }, progress.Values);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(Host, "")]
    public async Task Not_set_up_gives_one_information_line_and_a_completed_task(string host, string key)
    {
        _settings.Value = new ServiceSettings(host, key);
        using var services = Services();
        var progress = new RecordingProgress();

        await NewTask(services).ExecuteAsync(progress, CancellationToken.None);

        var line = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal(new double[] { 0, 100 }, progress.Values);
        Assert.False(Directory.Exists(_folder.File("data")));
    }

    [Fact]
    public async Task A_key_sealed_elsewhere_gives_one_information_line_and_a_completed_task()
    {
        _settings.Value = new ServiceSettings(Host, RenewalTestKit.Sealed(9, FakeDynu.Key));
        using var services = Services();

        await NewTask(services).ExecuteAsync(new RecordingProgress(), CancellationToken.None);

        var line = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("Re-enter your Dynu key", line.Text);
    }

    [Fact]
    public async Task A_build_failure_is_a_warning_and_the_task_still_completes()
    {
        SetUp();
        File.WriteAllText(_folder.File("data"), "in the way");
        using var services = Services();
        var progress = new RecordingProgress();

        await NewTask(services).ExecuteAsync(progress, CancellationToken.None);

        var line = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(new double[] { 0, 100 }, progress.Values);
    }

    [Fact]
    public async Task A_bug_in_the_manager_is_a_warning_and_never_reaches_Jellyfin()
    {
        SetUp();
        using var services = Services(renewer: () => new CannedRenewer(_ => throw new InvalidOperationException("boom with " + FakeDynu.Key)));
        var progress = new RecordingProgress();

        await NewTask(services).ExecuteAsync(progress, CancellationToken.None);

        var line = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(new double[] { 0, 100 }, progress.Values);

        // The exception's text, and the exception itself, stay out of the log.
        Assert.DoesNotContain(FakeDynu.Key, _log.All);
        Assert.DoesNotContain("boom", _log.All);
    }

    [Fact]
    public async Task Jellyfin_cancelling_the_task_is_the_one_thing_that_throws()
    {
        SetUp();
        using var services = Services(renewer: () => new CannedRenewer(async ct =>
        {
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, ct);
            return new ReconcileResult(ReconcileOutcome.UpToDate, null, null, null);
        }));
        using var cancel = new CancellationTokenSource();

        var run = NewTask(services).ExecuteAsync(new RecordingProgress(), cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Jellyfin_stopping_while_the_task_runs_ends_it_quietly()
    {
        SetUp();
        var issuer = new GatedIssuer();
        var services = Services(issuer: () => issuer);
        var run = NewTask(services).ExecuteAsync(new RecordingProgress(), CancellationToken.None);
        await issuer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        services.Dispose();

        // Its own token wasn't cancelled, so this is a stop, not a cancel: no throw, one line.
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Text.Contains("shutting down"));
    }

    [Fact]
    public async Task Two_task_runs_at_once_share_one_issuance()
    {
        SetUp();
        var issuer = new GatedIssuer();
        using var services = Services(issuer: () => issuer);

        var first = NewTask(services).ExecuteAsync(new RecordingProgress(), CancellationToken.None);
        await issuer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = NewTask(services).ExecuteAsync(new RecordingProgress(), CancellationToken.None);
        issuer.Gate.SetResult();
        await System.Threading.Tasks.Task.WhenAll(first, second);

        Assert.Equal(1, issuer.Calls);
    }

    [Fact]
    public void It_is_named_and_filed_for_the_dashboard_and_runs_daily_and_at_startup()
    {
        using var services = Services();
        var task = NewTask(services);

        Assert.Equal("Renew the HTTPS certificate", task.Name);
        Assert.Equal("Trailhead", task.Category);
        Assert.False(string.IsNullOrWhiteSpace(task.Key));
        Assert.False(string.IsNullOrWhiteSpace(task.Description));
        var triggers = task.GetDefaultTriggers().ToList();
        Assert.Equal(2, triggers.Count);
        var daily = Assert.Single(triggers, t => t.Type == TaskTriggerInfoType.DailyTrigger);
        Assert.InRange(daily.TimeOfDayTicks!.Value, 1, TimeSpan.FromDays(1).Ticks - 1);
        Assert.Single(triggers, t => t.Type == TaskTriggerInfoType.StartupTrigger);
    }
}

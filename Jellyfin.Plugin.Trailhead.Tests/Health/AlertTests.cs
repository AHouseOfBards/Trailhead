using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

/// <summary>The escalator's rules (T23, RASA health/escalate) with a fake clock.</summary>
public sealed class AlertTests : IDisposable
{
    private static readonly Fault Cert = new("certificate:expires_soon", "The certificate runs out soon.");
    private static readonly Fault Router = new("router:failed", "The router refused.");

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingSink _sink = new();

    public void Dispose() => _folder.Dispose();

    private string StatePath => _folder.File("alerts.json");

    private Escalator Make() => new(StatePath, _sink, _time, new ListLogger());

    private Task<AlertKind> ConsiderAsync(params Fault[] faults) => Make().ConsiderAsync(faults, CancellationToken.None);

    [Fact]
    public async Task A_server_that_has_always_been_fine_never_writes_a_word()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(AlertKind.None, await ConsiderAsync());
            _time.Advance(TimeSpan.FromDays(1));
        }

        Assert.Empty(_sink.All);
    }

    [Fact]
    public async Task A_problem_is_reported_when_it_starts_then_daily_then_once_when_it_clears()
    {
        Assert.Equal(AlertKind.Started, await ConsiderAsync(Cert));

        // Every ten minutes for most of a day: quiet.
        for (var i = 0; i < 143; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(AlertKind.None, await ConsiderAsync(Cert));
        }

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AlertKind.Still, await ConsiderAsync(Cert));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AlertKind.None, await ConsiderAsync(Cert));

        Assert.Equal(AlertKind.Cleared, await ConsiderAsync());
        Assert.Equal(AlertKind.None, await ConsiderAsync());

        Assert.Equal(new[] { AlertKind.Started, AlertKind.Still, AlertKind.Cleared }, _sink.All.Select(r => r.Kind));
        Assert.Contains("runs out soon", _sink.All[0].Overview, StringComparison.Ordinal);
        Assert.StartsWith("Trailhead", _sink.All[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_in_what_is_failing_is_reported_at_once()
    {
        await ConsiderAsync(Cert);
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(AlertKind.Started, await ConsiderAsync(Cert, Router));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AlertKind.Started, await ConsiderAsync(Router));
    }

    [Fact]
    public async Task The_order_of_the_faults_is_not_a_change()
    {
        await ConsiderAsync(Cert, Router);
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(AlertKind.None, await ConsiderAsync(Router, Cert));
    }

    [Fact]
    public async Task Nothing_is_said_to_have_cleared_when_nothing_was_reported()
    {
        Assert.Equal(AlertKind.None, await ConsiderAsync());
        Assert.False(File.Exists(StatePath));
        Assert.Empty(_sink.All);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"Signature\":42}")]
    public async Task An_unreadable_state_file_means_nothing_has_been_reported_yet(string content)
    {
        File.WriteAllText(StatePath, content);

        // Fine now: no "cleared" for a report nobody can prove was made.
        Assert.Equal(AlertKind.None, await ConsiderAsync());

        File.WriteAllText(StatePath, content);

        // Failing now: reported, rather than a damaged file silencing it for ever.
        Assert.Equal(AlertKind.Started, await ConsiderAsync(Cert));
    }

    [Fact]
    public async Task The_state_lives_in_the_file_so_a_restart_does_not_repeat_or_forget()
    {
        await ConsiderAsync(Cert);
        _time.Advance(TimeSpan.FromHours(1));

        // A new escalator, as after a Jellyfin restart.
        Assert.Equal(AlertKind.None, await ConsiderAsync(Cert));
        Assert.Equal(AlertKind.Cleared, await ConsiderAsync());
    }

    [Fact]
    public async Task A_log_that_refuses_is_not_retried_every_run()
    {
        _sink.Throws = true;

        Assert.Equal(AlertKind.Started, await ConsiderAsync(Cert));
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(AlertKind.None, await ConsiderAsync(Cert));
        Assert.Single(_sink.All);
    }

    [Fact]
    public void Activity_log_text_is_cut_to_what_Jellyfin_stores()
    {
        var cut = ActivityLogAlerts.Cut(new string('x', 600));

        Assert.Equal(512, cut.Length);
        Assert.Equal("short", ActivityLogAlerts.Cut("short"));
    }
}

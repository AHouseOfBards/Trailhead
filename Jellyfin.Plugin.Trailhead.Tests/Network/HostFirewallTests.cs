using Jellyfin.Plugin.Trailhead.Network;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Network;

/// <summary>
/// The firewall rules' commands and records. A recording runner stands in for the OS tool, so no real rule is ever
/// added on the machine running the tests.
/// </summary>
public sealed class HostFirewallTests : IDisposable
{
    private const string Program = @"C:\Program Files\Jellyfin\Server\jellyfin.exe";
    private const string Netsh = @"C:\Windows\system32\netsh.exe";

    private readonly TempFolder _folder = new();
    private readonly Runner _runner = new();

    public void Dispose() => _folder.Dispose();

    private sealed class Runner : IProcessRunner
    {
        internal List<CommandLine> Ran { get; } = new();

        /// <summary>Answers by the command's first distinguishing word; default exit 0.</summary>
        internal Func<CommandLine, ProcessResult> Answer { get; set; } = _ => new ProcessResult(0, string.Empty);

        internal Action? OnRun { get; set; }

        public Task<ProcessResult> RunAsync(CommandLine command, CancellationToken ct)
        {
            OnRun?.Invoke();
            Ran.Add(command);
            return Task.FromResult(Answer(command));
        }
    }

    private HostFirewall Make(FirewallEnvironment env) => new(_folder.File("firewall.json"), _runner, () => env, new FakeTimeProvider(), new ListLogger());

    private static FirewallEnvironment Windows(bool elevated) => new(HostOs.Windows, new Privilege(elevated, elevated, true), false, LinuxFirewall.None, Netsh, Program);

    private static FirewallEnvironment Linux(LinuxFirewall kind, bool root, string tool) => new(HostOs.Linux, new Privilege(root, root, root), false, kind, tool, "/usr/lib/jellyfin/bin/jellyfin");

    private static ProcessResult NotFound(CommandLine c) => c.Args.Contains("show") ? new ProcessResult(1, "No rules match the specified criteria.") : new ProcessResult(0, "Ok.");

    [Fact]
    public async Task Elevated_Windows_adds_a_named_rule_scoped_to_Jellyfin_and_the_port_for_Private_and_Domain_only()
    {
        _runner.Answer = NotFound;
        var firewall = Make(Windows(true));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Added, result.Outcome);
        Assert.Equal("Trailhead 8443", result.RuleName);
        var add = _runner.Ran.Last();
        Assert.Equal(Netsh, add.File);
        Assert.Equal(
            new[] { "advfirewall", "firewall", "add", "rule", "name=Trailhead 8443", "dir=in", "action=allow", "protocol=TCP", "localport=8443", "program=" + Program, "profile=private,domain", "enable=yes" },
            add.Args);
        var record = Assert.Single(firewall.Recorded);
        Assert.Equal(("Trailhead 8443", "netsh", 8443, "active"), (record.Name, record.Kind, record.Port, record.State));
    }

    [Fact]
    public async Task The_rule_is_recorded_before_netsh_is_run()
    {
        _runner.Answer = NotFound;
        var firewall = Make(Windows(true));
        var seen = new List<IReadOnlyList<FirewallRecord>>();
        _runner.OnRun = () => seen.Add(firewall.Recorded);

        await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        // show (nothing recorded yet), then add (pending already on disk).
        Assert.Empty(seen[0]);
        Assert.Equal("pending", Assert.Single(seen[1]).State);
    }

    [Fact]
    public async Task Not_elevated_Windows_runs_nothing_but_show_and_returns_the_one_command()
    {
        _runner.Answer = NotFound;
        var firewall = Make(Windows(false));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Manual, result.Outcome);
        Assert.Equal(
            "netsh advfirewall firewall add rule name=\"Trailhead 8443\" dir=in action=allow protocol=TCP localport=8443 program=\"" + Program + "\" profile=private,domain enable=yes",
            result.Command);
        Assert.All(_runner.Ran, c => Assert.Contains("show", c.Args));
        Assert.Equal("shown", Assert.Single(firewall.Recorded).State);
    }

    [Fact]
    public async Task A_rule_the_admin_added_from_the_command_is_found_by_name_and_removed_on_close()
    {
        var firewall = Make(Windows(true));

        var open = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);
        var close = await firewall.CloseAsync("8443", CancellationToken.None);

        Assert.Equal(FirewallOutcome.Added, open.Outcome);
        Assert.DoesNotContain(_runner.Ran, c => c.Args.Contains("add"));
        Assert.Equal(FirewallOutcome.Removed, close.Outcome);
        Assert.Equal(
            new[] { "advfirewall", "firewall", "delete", "rule", "name=Trailhead 8443", "dir=in", "protocol=TCP", "localport=8443", "program=" + Program },
            _runner.Ran.Last().Args);
        Assert.Empty(firewall.Recorded);
    }

    [Fact]
    public async Task A_failed_netsh_add_is_not_recorded_and_hands_back_the_command()
    {
        _runner.Answer = c => c.Args.Contains("show") ? new ProcessResult(1, "No rules match") : new ProcessResult(1, "The requested operation requires elevation.");
        var firewall = Make(Windows(true));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Command);
        Assert.Empty(firewall.Recorded);
    }

    [Fact]
    public async Task Closing_a_rule_that_was_never_recorded_runs_nothing()
    {
        var firewall = Make(Windows(true));

        var result = await firewall.CloseAsync("someone-elses", CancellationToken.None);

        Assert.Equal(FirewallOutcome.Removed, result.Outcome);
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public async Task Not_elevated_close_of_a_present_rule_returns_the_delete_command_and_keeps_the_record()
    {
        _runner.Answer = c => new ProcessResult(c.Args.Contains("show") ? 0 : 1, string.Empty);
        var firewall = Make(Windows(false));
        await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        var result = await firewall.CloseAsync("8443", CancellationToken.None);

        Assert.Equal(FirewallOutcome.Manual, result.Outcome);
        Assert.StartsWith("netsh advfirewall firewall delete rule name=\"Trailhead 8443\"", result.Command);
        Assert.Single(firewall.Recorded);
        Assert.DoesNotContain(_runner.Ran, c => c.Args.Contains("delete"));
    }

    [Fact]
    public async Task Ufw_as_root_adds_the_rule_with_the_name_as_its_comment()
    {
        var firewall = Make(Linux(LinuxFirewall.Ufw, true, "/usr/sbin/ufw"));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Added, result.Outcome);
        var run = Assert.Single(_runner.Ran);
        Assert.Equal("/usr/sbin/ufw", run.File);
        Assert.Equal(new[] { "allow", "8443/tcp", "comment", "Trailhead 8443" }, run.Args);
    }

    [Fact]
    public async Task A_port_ufw_already_allowed_is_the_admins_and_is_never_recorded_or_removed()
    {
        _runner.Answer = _ => new ProcessResult(0, "Skipping adding existing rule");
        var firewall = Make(Linux(LinuxFirewall.Ufw, true, "/usr/sbin/ufw"));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);
        await firewall.CloseAllAsync(CancellationToken.None);

        Assert.Equal(FirewallOutcome.AlreadyOpen, result.Outcome);
        Assert.Empty(firewall.Recorded);
        Assert.DoesNotContain(_runner.Ran, c => c.Args.Contains("delete"));
    }

    [Fact]
    public async Task A_ufw_rule_that_was_only_updated_was_the_admins_and_is_not_recorded()
    {
        // ufw matches a rule that differs only in its comment, and updates it rather than adding one.
        _runner.Answer = _ => new ProcessResult(0, "Rule updated\nRule updated (v6)");
        var firewall = Make(Linux(LinuxFirewall.Ufw, true, "/usr/sbin/ufw"));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.AlreadyOpen, result.Outcome);
        Assert.Empty(firewall.Recorded);
    }

    [Fact]
    public async Task Ufw_without_root_returns_the_sudo_line()
    {
        var firewall = Make(Linux(LinuxFirewall.Ufw, false, "/usr/sbin/ufw"));

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Manual, result.Outcome);
        Assert.Equal("sudo ufw allow 8443/tcp comment 'Trailhead 8443'", result.Command);
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public async Task Firewalld_adds_permanent_and_runtime_without_a_reload_and_leaves_an_open_port_alone()
    {
        var firewall = Make(Linux(LinuxFirewall.Firewalld, true, "/usr/bin/firewall-cmd"));
        _runner.Answer = c => new ProcessResult(c.Args[0].StartsWith("--query", StringComparison.Ordinal) ? 1 : 0, string.Empty);

        var added = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Added, added.Outcome);
        Assert.Equal(
            new[] { "--query-port=8443/tcp", "--permanent --add-port=8443/tcp", "--add-port=8443/tcp" },
            _runner.Ran.Select(c => string.Join(' ', c.Args)));
        Assert.DoesNotContain(_runner.Ran, c => c.Args.Contains("--reload"));

        _runner.Ran.Clear();
        _runner.Answer = _ => new ProcessResult(0, "yes");
        var open = await firewall.OpenAsync("9443", 9443, false, CancellationToken.None);
        Assert.Equal(FirewallOutcome.AlreadyOpen, open.Outcome);
        Assert.DoesNotContain(firewall.Recorded, r => r.Port == 9443);
    }

    [Fact]
    public async Task Linux_with_no_firewall_needs_nothing()
    {
        var firewall = Make(Linux(LinuxFirewall.None, true, string.Empty) with { Tool = null });

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.NothingNeeded, result.Outcome);
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public async Task Docker_returns_the_ports_lines_host_443_and_8443_to_the_containers_8443()
    {
        var firewall = Make(Linux(LinuxFirewall.Ufw, true, "/usr/sbin/ufw") with { InDocker = true });

        var result = await firewall.OpenAsync("8443", 8443, true, CancellationToken.None);

        Assert.Equal(FirewallOutcome.Docker, result.Outcome);
        Assert.Equal("ports:\n  - \"443:8443\"\n  - \"8443:8443\"\n", result.Command);
        Assert.Empty(_runner.Ran);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8443 dir=out")]
    [InlineData("a\"b")]
    [InlineData("name=x")]
    public async Task A_rule_id_that_could_change_the_command_is_refused(string id)
    {
        var firewall = Make(Windows(true));

        await Assert.ThrowsAsync<ArgumentException>(() => firewall.OpenAsync(id, 8443, true, CancellationToken.None));
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public async Task A_program_path_with_a_quote_is_refused()
    {
        var firewall = Make(Windows(true) with { ProgramPath = "C:\\x\" dir=out \"y.exe" });

        await Assert.ThrowsAsync<ArgumentException>(() => firewall.OpenAsync("8443", 8443, true, CancellationToken.None));
        Assert.Empty(_runner.Ran);
    }

    [Fact]
    public void The_netsh_used_is_the_one_in_the_system_folder()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "netsh.exe"), FirewallCommands.Netsh(Environment.SystemDirectory));
    }
}

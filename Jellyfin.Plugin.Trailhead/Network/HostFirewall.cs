using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Campground.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Network;

/// <summary>A program and its arguments, kept apart: arguments go to the OS as a list, never through a shell string.</summary>
internal sealed record CommandLine(string File, IReadOnlyList<string> Args);

internal sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs a built-in OS tool. A seam: tests check the arguments and never add a real rule.</summary>
internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(CommandLine command, CancellationToken ct);
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    internal static readonly SystemProcessRunner Instance = new();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int MaxOutput = 64 * 1024;

    public async Task<ProcessResult> RunAsync(CommandLine command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command.File)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in command.Args)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The program didn't start.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        var text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return new ProcessResult(process.ExitCode, text.Length > MaxOutput ? text[..MaxOutput] : text);
    }
}

internal enum HostOs
{
    Other,
    Windows,
    Linux,
}

internal enum LinuxFirewall
{
    None,
    Ufw,
    Firewalld,
}

/// <summary>What decides how a rule is made (§4). Detected once per call; a test builds any combination.</summary>
/// <param name="Tool">The firewall program's full path in a system folder, or null (S3: never a writable path).</param>
internal sealed record FirewallEnvironment(HostOs Os, Privilege Privilege, bool InDocker, LinuxFirewall Linux, string? Tool, string ProgramPath);

internal enum FirewallOutcome
{
    /// <summary>Trailhead added the rule (or found its own rule by name) and recorded it.</summary>
    Added,

    /// <summary>The port was already open by a rule that isn't Trailhead's. Not recorded, so Remove leaves it.</summary>
    AlreadyOpen,

    /// <summary>No host firewall is active (Linux without ufw or firewalld).</summary>
    NothingNeeded,

    /// <summary>Trailhead may not change the firewall: <see cref="FirewallResult.Command"/> is the one command for the admin.</summary>
    Manual,

    /// <summary>In Docker the host side is the compose <c>ports:</c> entry, in <see cref="FirewallResult.Command"/> (T14).</summary>
    Docker,

    /// <summary>Removed (or already gone) and taken off the list.</summary>
    Removed,

    /// <summary>The tool said no. The rule wasn't recorded (open) or stays recorded (close).</summary>
    Failed,
}

internal sealed record FirewallResult(FirewallOutcome Outcome, string RuleName, string? Command = null);

internal static class FirewallKinds
{
    internal const string Netsh = "netsh";
    internal const string Ufw = "ufw";
    internal const string Firewalld = "firewalld";
}

/// <param name="State">"active" once the tool said yes; "pending" just before it is asked; "shown" when the admin was given the command.</param>
internal sealed record FirewallRecord(string Id, string Name, string Kind, int Port, string Protocol, string Program, string State, DateTimeOffset RecordedAt);

internal sealed class FirewallFile
{
    public List<FirewallRecord> Rules { get; set; } = new();
}

/// <summary>
/// The commands, built as argument lists (T27, §4). Windows: <c>netsh advfirewall firewall</c>, inbound TCP, scoped to
/// Jellyfin's program and the port, profiles Private and Domain only (a Public network is reported, T35, not opened).
/// Linux: ufw (the rule carries the name as its comment) or firewalld (a port has no name there, so the name lives
/// only in Trailhead's list, and a port that was already open is never recorded as ours).
/// </summary>
internal static partial class FirewallCommands
{
    internal static string RuleName(string id) => "Trailhead " + id;

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex IdPattern();

    internal static void CheckId(string id)
    {
        if (!IdPattern().IsMatch(id))
        {
            throw new ArgumentException("A rule id is 1 to 40 letters, digits or hyphens.", nameof(id));
        }
    }

    internal static void CheckProgram(string program)
    {
        // A quote would end the value in the command shown to the admin. No Windows path has one.
        if (program.Length == 0 || program.Contains('"', StringComparison.Ordinal) || program.Contains('\n', StringComparison.Ordinal) || program.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException("The program path can't be used in a firewall rule.", nameof(program));
        }
    }

    internal static string Netsh(string systemDirectory) => Path.Combine(systemDirectory, "netsh.exe");

    internal static CommandLine WindowsAdd(string netsh, string name, int port, string program) => new(netsh, new[]
    {
        "advfirewall", "firewall", "add", "rule",
        "name=" + name, "dir=in", "action=allow", "protocol=TCP", "localport=" + Port(port), "program=" + program,
        "profile=private,domain", "enable=yes",
    });

    internal static CommandLine WindowsDelete(string netsh, string name, int port, string program) => new(netsh, new[]
    {
        "advfirewall", "firewall", "delete", "rule", "name=" + name, "dir=in", "protocol=TCP", "localport=" + Port(port), "program=" + program,
    });

    internal static CommandLine WindowsShow(string netsh, string name) => new(netsh, new[] { "advfirewall", "firewall", "show", "rule", "name=" + name });

    internal static CommandLine UfwAdd(string ufw, string name, int port) => new(ufw, new[] { "allow", Port(port) + "/tcp", "comment", name });

    internal static CommandLine UfwDelete(string ufw, int port) => new(ufw, new[] { "delete", "allow", Port(port) + "/tcp" });

    internal static CommandLine FirewalldQuery(string tool, int port) => new(tool, new[] { "--query-port=" + Port(port) + "/tcp" });

    // Permanent and runtime separately: a --reload would also drop every other runtime-only rule on the machine.
    internal static CommandLine[] FirewalldAdd(string tool, int port) => new[]
    {
        new CommandLine(tool, new[] { "--permanent", "--add-port=" + Port(port) + "/tcp" }),
        new CommandLine(tool, new[] { "--add-port=" + Port(port) + "/tcp" }),
    };

    internal static CommandLine[] FirewalldRemove(string tool, int port) => new[]
    {
        new CommandLine(tool, new[] { "--permanent", "--remove-port=" + Port(port) + "/tcp" }),
        new CommandLine(tool, new[] { "--remove-port=" + Port(port) + "/tcp" }),
    };

    /// <summary>The command an admin types, as one line. Windows: in an administrator prompt. Linux: with sudo.</summary>
    internal static string ForAdmin(string kind, bool add, string name, int port, string program) => kind switch
    {
        FirewallKinds.Netsh => add
            ? $"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={Port(port)} program=\"{program}\" profile=private,domain enable=yes"
            : $"netsh advfirewall firewall delete rule name=\"{name}\" dir=in protocol=TCP localport={Port(port)} program=\"{program}\"",
        FirewallKinds.Ufw => add
            ? $"sudo ufw allow {Port(port)}/tcp comment '{name}'"
            : $"sudo ufw delete allow {Port(port)}/tcp",
        _ => add
            ? $"sudo firewall-cmd --permanent --add-port={Port(port)}/tcp && sudo firewall-cmd --add-port={Port(port)}/tcp"
            : $"sudo firewall-cmd --permanent --remove-port={Port(port)}/tcp && sudo firewall-cmd --remove-port={Port(port)}/tcp",
    };

    /// <summary>The compose <c>ports:</c> entries: host 443 to the container's port when asked, and the port to itself.</summary>
    internal static string DockerPorts(int port, bool alsoHost443)
    {
        var text = new StringBuilder("ports:\n");
        if (alsoHost443)
        {
            text.Append("  - \"443:").Append(Port(port)).Append("\"\n");
        }

        text.Append("  - \"").Append(Port(port)).Append(':').Append(Port(port)).Append("\"\n");
        return text.ToString();
    }

    private static string Port(int port) => port is >= 1 and <= 65535
        ? port.ToString(CultureInfo.InvariantCulture)
        : throw new ArgumentOutOfRangeException(nameof(port), "A port is 1 to 65535.");
}

/// <summary>
/// Host firewall rules for Trailhead's ports (§4, T27). Every rule it adds is recorded first, named
/// <c>Trailhead &lt;id&gt;</c>, and removed on Remove. A rule the admin made from Trailhead's command is found by that
/// name and treated the same way. Nothing here ever asks anyone to run a program from a writable path (S3).
/// </summary>
internal sealed class HostFirewall
{
    private readonly AtomicJsonFile _file;
    private readonly IProcessRunner _runner;
    private readonly Func<FirewallEnvironment> _environment;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    internal HostFirewall(string path, IProcessRunner runner, Func<FirewallEnvironment> environment, TimeProvider time, ILogger logger)
    {
        _file = new AtomicJsonFile(path);
        _runner = runner;
        _environment = environment;
        _time = time;
        _logger = logger;
    }

    internal IReadOnlyList<FirewallRecord> Recorded => _file.Load<FirewallFile>()?.Rules ?? new List<FirewallRecord>();

    /// <summary>What this machine is, for <see cref="FirewallEnvironment"/>. Tool paths only in root-owned system folders.</summary>
    internal static FirewallEnvironment Detect(IFileProbe files, IReadOnlyList<Nic> nics)
    {
        var privilege = Elevation.Current(files);
        var program = Environment.ProcessPath ?? string.Empty;
        var docker = ContainerCheck.InContainer(files);
        if (OperatingSystem.IsWindows())
        {
            return new FirewallEnvironment(HostOs.Windows, privilege, docker, LinuxFirewall.None, FirewallCommands.Netsh(Environment.SystemDirectory), program);
        }

        if (!OperatingSystem.IsLinux())
        {
            return new FirewallEnvironment(HostOs.Other, privilege, docker, LinuxFirewall.None, null, program);
        }

        var ufwOn = (files.ReadText("/etc/ufw/ufw.conf") ?? string.Empty).Split('\n').Any(l => l.Trim() is "ENABLED=yes" or "ENABLED=\"yes\"");
        var ufw = new[] { "/usr/sbin/ufw", "/sbin/ufw" }.FirstOrDefault(files.Exists);
        if (ufwOn && ufw is not null)
        {
            return new FirewallEnvironment(HostOs.Linux, privilege, docker, LinuxFirewall.Ufw, ufw, program);
        }

        var tool = new[] { "/usr/bin/firewall-cmd", "/bin/firewall-cmd", "/usr/sbin/firewall-cmd" }.FirstOrDefault(files.Exists);
        if (tool is not null && FirewalldRunning(files))
        {
            return new FirewallEnvironment(HostOs.Linux, privilege, docker, LinuxFirewall.Firewalld, tool, program);
        }

        return new FirewallEnvironment(HostOs.Linux, privilege, docker, LinuxFirewall.None, null, program);
    }

    private static bool FirewalldRunning(IFileProbe files)
    {
        foreach (var pidFile in new[] { "/run/firewalld.pid", "/var/run/firewalld.pid" })
        {
            var pid = files.ReadText(pidFile)?.Trim();
            if (!string.IsNullOrEmpty(pid) && int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out _) && files.Exists("/proc/" + pid + "/status"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Opens <paramref name="port"/> (TCP) for Jellyfin. <paramref name="alsoHost443"/> only shapes the Docker line.</summary>
    internal async Task<FirewallResult> OpenAsync(string id, int port, bool alsoHost443, CancellationToken ct)
    {
        FirewallCommands.CheckId(id);
        var name = FirewallCommands.RuleName(id);
        var env = _environment();

        if (env.InDocker)
        {
            return new FirewallResult(FirewallOutcome.Docker, name, FirewallCommands.DockerPorts(port, alsoHost443));
        }

        switch (env.Os)
        {
            case HostOs.Windows when env.Tool is { } netsh:
                FirewallCommands.CheckProgram(env.ProgramPath);
                if (await SucceedsAsync(FirewallCommands.WindowsShow(netsh, name), ct).ConfigureAwait(false))
                {
                    // Ours by its name: the admin ran Trailhead's command, or an earlier run added it.
                    Put(new FirewallRecord(id, name, FirewallKinds.Netsh, port, "TCP", env.ProgramPath, "active", Now));
                    return new FirewallResult(FirewallOutcome.Added, name);
                }

                if (!env.Privilege.Elevated)
                {
                    Put(new FirewallRecord(id, name, FirewallKinds.Netsh, port, "TCP", env.ProgramPath, "shown", Now));
                    return new FirewallResult(FirewallOutcome.Manual, name, FirewallCommands.ForAdmin(FirewallKinds.Netsh, true, name, port, env.ProgramPath));
                }

                return await AddAsync(new FirewallRecord(id, name, FirewallKinds.Netsh, port, "TCP", env.ProgramPath, "pending", Now), new[] { FirewallCommands.WindowsAdd(netsh, name, port, env.ProgramPath) }, ct).ConfigureAwait(false);

            case HostOs.Linux when env.Linux == LinuxFirewall.Ufw && env.Tool is { } ufw:
                if (!env.Privilege.NetAdmin)
                {
                    Put(new FirewallRecord(id, name, FirewallKinds.Ufw, port, "TCP", env.ProgramPath, "shown", Now));
                    return new FirewallResult(FirewallOutcome.Manual, name, FirewallCommands.ForAdmin(FirewallKinds.Ufw, true, name, port, env.ProgramPath));
                }

                return await AddAsync(new FirewallRecord(id, name, FirewallKinds.Ufw, port, "TCP", env.ProgramPath, "pending", Now), new[] { FirewallCommands.UfwAdd(ufw, name, port) }, ct).ConfigureAwait(false);

            case HostOs.Linux when env.Linux == LinuxFirewall.Firewalld && env.Tool is { } fwd:
                if (!env.Privilege.NetAdmin)
                {
                    Put(new FirewallRecord(id, name, FirewallKinds.Firewalld, port, "TCP", env.ProgramPath, "shown", Now));
                    return new FirewallResult(FirewallOutcome.Manual, name, FirewallCommands.ForAdmin(FirewallKinds.Firewalld, true, name, port, env.ProgramPath));
                }

                if (await SucceedsAsync(FirewallCommands.FirewalldQuery(fwd, port), ct).ConfigureAwait(false))
                {
                    return new FirewallResult(FirewallOutcome.AlreadyOpen, name);
                }

                return await AddAsync(new FirewallRecord(id, name, FirewallKinds.Firewalld, port, "TCP", env.ProgramPath, "pending", Now), FirewallCommands.FirewalldAdd(fwd, port), ct).ConfigureAwait(false);

            default:
                return new FirewallResult(FirewallOutcome.NothingNeeded, name);
        }
    }

    /// <summary>Removes one recorded rule. Without the right to, returns the one command and keeps the record.</summary>
    internal async Task<FirewallResult> CloseAsync(string id, CancellationToken ct)
    {
        var record = Recorded.FirstOrDefault(r => r.Id == id);
        if (record is null)
        {
            return new FirewallResult(FirewallOutcome.Removed, FirewallCommands.RuleName(id));
        }

        var env = _environment();
        var command = FirewallCommands.ForAdmin(record.Kind, false, record.Name, record.Port, record.Program);
        switch (record.Kind)
        {
            case FirewallKinds.Netsh when env.Os == HostOs.Windows && env.Tool is { } netsh:
                if (!await SucceedsAsync(FirewallCommands.WindowsShow(netsh, record.Name), ct).ConfigureAwait(false))
                {
                    // Gone already (or never added from the command shown): nothing left of ours.
                    return Forget(record);
                }

                if (!env.Privilege.Elevated)
                {
                    return new FirewallResult(FirewallOutcome.Manual, record.Name, command);
                }

                return await RemoveAsync(record, new[] { FirewallCommands.WindowsDelete(netsh, record.Name, record.Port, record.Program) }, ct).ConfigureAwait(false);

            case FirewallKinds.Ufw when env.Tool is { } ufw && env.Linux == LinuxFirewall.Ufw:
                if (record.State == "shown" || !env.Privilege.NetAdmin)
                {
                    // ufw won't list rules without root, so a rule the admin may have added can't be checked for.
                    return new FirewallResult(FirewallOutcome.Manual, record.Name, command);
                }

                return await RemoveAsync(record, new[] { FirewallCommands.UfwDelete(ufw, record.Port) }, ct).ConfigureAwait(false);

            case FirewallKinds.Firewalld when env.Tool is { } fwd && env.Linux == LinuxFirewall.Firewalld:
                if (record.State == "shown" || !env.Privilege.NetAdmin)
                {
                    return new FirewallResult(FirewallOutcome.Manual, record.Name, command);
                }

                return await RemoveAsync(record, FirewallCommands.FirewalldRemove(fwd, record.Port), ct).ConfigureAwait(false);

            default:
                // The firewall it was made in isn't there any more (ufw switched off, moved machine): show the command.
                return new FirewallResult(FirewallOutcome.Manual, record.Name, command);
        }
    }

    internal async Task<IReadOnlyList<FirewallResult>> CloseAllAsync(CancellationToken ct)
    {
        var results = new List<FirewallResult>();
        foreach (var r in Recorded)
        {
            results.Add(await CloseAsync(r.Id, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<FirewallResult> AddAsync(FirewallRecord pending, IReadOnlyList<CommandLine> commands, CancellationToken ct)
    {
        // Recorded first: if Jellyfin stops mid-way, Remove still knows to look for it.
        Put(pending);
        foreach (var command in commands)
        {
            var result = await RunAsync(command, ct).ConfigureAwait(false);
            // ufw's set_rule (src/backend_iptables.py, trunk) answers "Skipping adding existing rule" for an identical
            // rule, and "Rule updated" when only the comment differs, having put our comment on the admin's rule.
            // Either way the rule was the admin's, so it isn't recorded and Remove leaves it.
            if (pending.Kind == FirewallKinds.Ufw
                && (result.Output.Contains("Skipping adding existing rule", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("Rule updated", StringComparison.OrdinalIgnoreCase)))
            {
                // The admin's own rule already allows it. Not ours, so Remove must not delete it.
                Forget(pending);
                return new FirewallResult(FirewallOutcome.AlreadyOpen, pending.Name);
            }

            if (result.ExitCode != 0)
            {
                Forget(pending);
                _logger.LogWarning("Trailhead couldn't add the firewall rule {Name} (exit code {Code}).", pending.Name, result.ExitCode);
                return new FirewallResult(FirewallOutcome.Failed, pending.Name, FirewallCommands.ForAdmin(pending.Kind, true, pending.Name, pending.Port, pending.Program));
            }
        }

        Put(pending with { State = "active" });
        _logger.LogInformation("Trailhead added the firewall rule {Name} for port {Port}.", pending.Name, pending.Port);
        return new FirewallResult(FirewallOutcome.Added, pending.Name);
    }

    private async Task<FirewallResult> RemoveAsync(FirewallRecord record, IReadOnlyList<CommandLine> commands, CancellationToken ct)
    {
        foreach (var command in commands)
        {
            var result = await RunAsync(command, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 && !result.Output.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
                && !result.Output.Contains("Could not delete non-existent rule", StringComparison.OrdinalIgnoreCase)
                && !result.Output.Contains("NOT_ENABLED", StringComparison.Ordinal))
            {
                _logger.LogWarning("Trailhead couldn't remove the firewall rule {Name} (exit code {Code}).", record.Name, result.ExitCode);
                return new FirewallResult(FirewallOutcome.Failed, record.Name, FirewallCommands.ForAdmin(record.Kind, false, record.Name, record.Port, record.Program));
            }
        }

        _logger.LogInformation("Trailhead removed the firewall rule {Name}.", record.Name);
        return Forget(record);
    }

    private FirewallResult Forget(FirewallRecord record)
    {
        var all = Recorded.Where(r => r.Id != record.Id).ToList();
        _file.Save(new FirewallFile { Rules = all });
        return new FirewallResult(FirewallOutcome.Removed, record.Name);
    }

    private void Put(FirewallRecord record)
    {
        var all = Recorded.Where(r => r.Id != record.Id).ToList();
        all.Add(record);
        _file.Save(new FirewallFile { Rules = all });
    }

    private async Task<bool> SucceedsAsync(CommandLine command, CancellationToken ct)
        => (await RunAsync(command, ct).ConfigureAwait(false)).ExitCode == 0;

    private async Task<ProcessResult> RunAsync(CommandLine command, CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(command, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning("Trailhead couldn't run the firewall tool ({Error}).", e.GetType().Name);
            return new ProcessResult(-1, string.Empty);
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();
}

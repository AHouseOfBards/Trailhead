using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Jellyfin.Plugin.Trailhead.Network;

/// <summary>What Jellyfin's process may do, decided from its own token, not the install mode (§4, GameTent #17).</summary>
internal sealed record Privilege(bool Elevated, bool NetAdmin, bool BindLowPorts);

internal static class Elevation
{
    // Linux capability bits (linux/capability.h).
    private const int CapNetBindService = 10;
    private const int CapNetAdmin = 12;

    /// <summary>
    /// Windows: an elevated token only. A member of Administrators running without elevation gets false, which is
    /// right: netsh would refuse it. Linux: root, or the capability itself (<c>CapEff</c> in /proc/self/status).
    /// Anything that can't be read counts as not privileged, so Trailhead shows the command rather than failing.
    /// </summary>
    internal static Privilege Current(IFileProbe files)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var admin = IsWindowsAdmin();
                return new Privilege(admin, admin, true);
            }

            if (OperatingSystem.IsLinux())
            {
                // The effective uid from /proc rather than a geteuid() P/Invoke: the C library's file name differs
                // between glibc and musl (Alpine), and a file read can't fail to load.
                var status = files.ReadText("/proc/self/status");
                var root = EffectiveUid(status) == 0;
                var caps = EffectiveCaps(status);
                return new Privilege(root, root || Has(caps, CapNetAdmin), root || Has(caps, CapNetBindService));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return new Privilege(false, false, false);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The effective capability mask from /proc/self/status, or 0.</summary>
    internal static ulong EffectiveCaps(string? status)
    {
        foreach (var line in (status ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("CapEff:", StringComparison.Ordinal)
                && ulong.TryParse(line["CapEff:".Length..].Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mask))
            {
                return mask;
            }
        }

        return 0;
    }

    internal static bool Has(ulong caps, int bit) => (caps & (1UL << bit)) != 0;

    /// <summary>The effective uid ("Uid:" real, effective, saved, fs) from /proc/self/status, or null.</summary>
    internal static long? EffectiveUid(string? status)
    {
        foreach (var line in (status ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                var f = line["Uid:".Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return f.Length >= 2 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
            }
        }

        return null;
    }
}

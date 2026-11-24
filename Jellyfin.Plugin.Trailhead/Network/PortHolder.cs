using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Trailhead.Network;

/// <summary>A local TCP port: free, or held, and by what when the OS will say.</summary>
/// <param name="Holder">A name a person may recognise ("IIS", "UniFi Network", "nginx"), or null when unknown.</param>
internal sealed record PortState(int Port, bool Free, int? ProcessId, string? Holder);

/// <summary>
/// Is a TCP port free here, and if not, what holds it (RASA's <c>probe/holder</c>, "It looks like UniFi", #20 M5).
/// RASA shelled out to netstat and tasklist; here it is the OS's own table (iphlpapi on Windows, /proc on Linux), so
/// no program is started. Naming the holder is best effort: process details often need privileges, and "something
/// holds it" is still worth saying.
/// </summary>
internal static class PortHolder
{
    internal static PortState Check(int port)
    {
        var free = IsFree(port);
        if (free)
        {
            return new PortState(port, true, null, null);
        }

        int? pid = null;
        try
        {
            pid = OperatingSystem.IsWindows() ? WindowsListenerPid(port) : OperatingSystem.IsLinux() ? LinuxListenerPid(port) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
        }

        return new PortState(port, false, pid, pid is { } p ? NameOf(p) : null);
    }

    /// <summary>
    /// Free only when nothing listens on it and both the wildcard and loopback binds work. One bind isn't enough: on
    /// Windows a wildcard bind succeeds beside another process's 127.0.0.1 listener (RASA's finding), and busy is the
    /// safe answer to get wrong.
    /// </summary>
    internal static bool IsFree(int port)
    {
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port))
            {
                return false;
            }
        }
        catch (NetworkInformationException)
        {
        }

        foreach (var address in new[] { IPAddress.Any, IPAddress.Loopback, IPAddress.IPv6Any })
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            {
                continue;
            }

            try
            {
                using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                if (OperatingSystem.IsWindows())
                {
                    socket.ExclusiveAddressUse = true;
                }

                if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = false;
                }

                socket.Bind(new IPEndPoint(address, port));
                socket.Listen(1);
            }
            catch (SocketException)
            {
                return false;
            }
        }

        return true;
    }

    internal static string? NameOf(int pid)
    {
        string? name = null;
        string? path = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Another user's process: the name is all there is.
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        if (OperatingSystem.IsLinux())
        {
            path ??= ReadCmdline(pid);
        }

        return Friendly(name, path);
    }

    /// <summary>A name a person may recognise, from the process name and (when readable) its path or command line.</summary>
    internal static string? Friendly(string? name, string? path)
    {
        var where = (path ?? string.Empty).ToLowerInvariant();
        if (where.Contains("unifi", StringComparison.Ordinal))
        {
            return "UniFi Network";
        }

        var exe = (name ?? string.Empty).ToLowerInvariant();
        if (exe.EndsWith(".exe", StringComparison.Ordinal))
        {
            exe = exe[..^4];
        }

        return exe switch
        {
            "" => null,

            // 443 held by "System" is http.sys, which IIS, WinRM, BranchCache and others sit behind (RASA).
            "system" => "a built-in Windows service",
            "w3wp" or "inetinfo" or "iisexpress" => "IIS",
            "httpd" or "apache2" => "Apache",
            "nginx" => "nginx",
            "caddy" => "Caddy",
            "traefik" => "Traefik",
            "haproxy" => "HAProxy",
            "lighttpd" => "lighttpd",
            "com.docker.backend" or "dockerd" or "docker" or "docker-proxy" or "containerd" or "wslrelay" => "Docker",
            "vmware-hostd" => "VMware",
            "jellyfin" => "Jellyfin",
            "plex media server" or "plexmediaserver" => "Plex",
            _ => name,
        };
    }

    private static string? ReadCmdline(int pid)
    {
        try
        {
            return File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/cmdline").Replace('\0', ' ');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Linux: /proc/net/tcp{,6} lists listeners (state 0A) with their socket inode; /proc/<pid>/fd links to
    // "socket:[inode]". Only this user's processes are readable without privileges, which is the usual case anyway
    // (Jellyfin vs. another service under the same account), and anything else is "unknown".
    private static int? LinuxListenerPid(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table))
            {
                continue;
            }

            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10 || f[3] != "0A")
                {
                    continue;
                }

                var colon = f[1].LastIndexOf(':');
                if (colon > 0 && int.TryParse(f[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) && p == port)
                {
                    inodes.Add("socket:[" + f[9] + "]");
                }
            }
        }

        if (inodes.Count == 0)
        {
            return null;
        }

        var deadline = Stopwatch.StartNew();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(2))
            {
                break;
            }

            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(dir, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget is { } target && inodes.Contains(target))
                    {
                        return pid;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    // Windows: GetExtendedTcpTable with TCP_TABLE_OWNER_PID_LISTENER, IPv4 then IPv6. Needs no elevation.
    private static int? WindowsListenerPid(int port)
        => WindowsListenerPid(port, AfInet, 24, 8, 20) ?? WindowsListenerPid(port, AfInet6, 56, 20, 52);

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;

    private static int? WindowsListenerPid(int port, int family, int rowSize, int portOffset, int pidOffset)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0);
                if (result == 122)
                {
                    // ERROR_INSUFFICIENT_BUFFER: the table grew in between.
                    continue;
                }

                if (result != 0)
                {
                    return null;
                }

                var bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, size);
                var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                for (var i = 0; i < count; i++)
                {
                    var row = 4 + (i * rowSize);
                    if (row + rowSize > bytes.Length)
                    {
                        break;
                    }

                    // The port is in network order in the low two bytes of a DWORD.
                    var rowPort = (bytes[row + portOffset] << 8) | bytes[row + portOffset + 1];
                    if (rowPort == port)
                    {
                        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(row + pidOffset));
                    }
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}

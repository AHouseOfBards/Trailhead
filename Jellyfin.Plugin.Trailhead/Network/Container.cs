using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Trailhead.Network;

internal enum ContainerNetwork
{
    /// <summary>Not in a container.</summary>
    None,

    /// <summary>In a container on a bridge network: SSDP can't reach the router, and the host side is the <c>ports:</c> line (T14).</summary>
    Bridge,

    /// <summary>In a container with the host's network: UPnP works as on the host.</summary>
    Host,

    /// <summary>In a container, but the network mode can't be told apart.</summary>
    Unknown,
}

/// <summary>Reads files for the container check. A seam, so a test can be any machine.</summary>
internal interface IFileProbe
{
    bool Exists(string path);

    string? ReadText(string path);
}

internal sealed class SystemFileProbe : IFileProbe
{
    internal static readonly SystemFileProbe Instance = new();

    public bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // /proc files report length 0, so this reads to the end rather than trusting the size; capped all the same.
            using var reader = new StreamReader(path);
            var buffer = new char[64 * 1024];
            var n = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Docker (and Podman) detection, from RASA's <c>probe/host.go</c>, plus whether the network is a bridge.</summary>
internal static class ContainerCheck
{
    private static readonly string[] CgroupMarkers = { "docker", "containerd", "kubepods", "lxc", "podman", "libpod" };

    internal static bool InContainer(IFileProbe files)
    {
        if (files.Exists("/.dockerenv") || files.Exists("/run/.containerenv"))
        {
            return true;
        }

        // cgroup v1 names the container in PID 1's cgroups. Under v2 that file is just "0::/", but the mounts still
        // show Docker's own paths (/var/lib/docker/containers/<id>/hostname and friends).
        var cgroup = files.ReadText("/proc/1/cgroup") ?? string.Empty;
        if (CgroupMarkers.Any(m => cgroup.Contains(m, StringComparison.Ordinal)))
        {
            return true;
        }

        var mounts = files.ReadText("/proc/self/mountinfo") ?? string.Empty;
        return mounts.Contains("/docker/containers/", StringComparison.Ordinal) || mounts.Contains("/containers/storage/", StringComparison.Ordinal);
    }

    /// <summary>
    /// How the container is networked, from what it can see. With host networking the host's own bridges (docker0,
    /// br-…) and veth pairs are visible; on a bridge network there is only the container's end (eth0) on a private
    /// subnet whose gateway is the bridge. Anything else is Unknown, and the page asks rather than guesses.
    /// </summary>
    internal static ContainerNetwork Network(IFileProbe files, IReadOnlyList<Nic> nics)
    {
        if (!InContainer(files))
        {
            return ContainerNetwork.None;
        }

        var up = nics.Where(n => n.Up && n.Type != System.Net.NetworkInformation.NetworkInterfaceType.Loopback).ToList();
        if (up.Any(n => n.Name == "docker0" || n.Name.StartsWith("br-", StringComparison.Ordinal) || n.Name.StartsWith("veth", StringComparison.Ordinal)))
        {
            return ContainerNetwork.Host;
        }

        var v4 = up.SelectMany(n => n.Addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => (Nic: n, a.Address))).ToList();
        if (v4.Count > 0 && v4.All(x => IsDockerRange(x.Address)) && up.All(n => n.Name.StartsWith("eth", StringComparison.Ordinal)))
        {
            return ContainerNetwork.Bridge;
        }

        return ContainerNetwork.Unknown;
    }

    // Docker's default address pools: 172.17.0.0/16 onwards inside 172.16.0.0/12, then 192.168.0.0/16 in /20s.
    private static bool IsDockerRange(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168 && (b[2] & 0x0F) == 0 && b[2] >= 16);
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Trailhead.Network;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Network;

/// <summary>The port check and the holder lookup on this machine's real sockets and process table.</summary>
public sealed class PortHolderTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void A_port_this_process_listens_on_is_busy_and_names_this_process()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var state = PortHolder.Check(port);

        Assert.False(state.Free);
        Assert.Equal(Environment.ProcessId, state.ProcessId);
        using var me = Process.GetCurrentProcess();
        Assert.Equal(PortHolder.Friendly(me.ProcessName, null), state.Holder);
    }

    [Fact]
    public void A_listener_on_loopback_only_still_makes_the_port_busy()
    {
        // On Windows a wildcard bind succeeds beside it (RASA's finding), so a wildcard-only check would say free.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(PortHolder.IsFree(port));
    }

    [Fact]
    public void An_unused_port_is_free_with_no_holder()
    {
        var port = FreePort();

        var state = PortHolder.Check(port);

        Assert.True(state.Free);
        Assert.Null(state.Holder);
    }

    [Theory]
    [InlineData("System", null, "a built-in Windows service")]
    [InlineData("w3wp.exe", null, "IIS")]
    [InlineData("nginx", null, "nginx")]
    [InlineData("java", "C:\\Program Files\\Ubiquiti UniFi\\lib\\ace.jar", "UniFi Network")]
    [InlineData("java", "/usr/lib/unifi/bin/unifi.init", "UniFi Network")]
    [InlineData("docker-proxy", null, "Docker")]
    [InlineData("something", null, "something")]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void Holders_are_named_as_a_person_would_know_them(string? name, string? path, string? expected)
        => Assert.Equal(expected, PortHolder.Friendly(name, path));
}

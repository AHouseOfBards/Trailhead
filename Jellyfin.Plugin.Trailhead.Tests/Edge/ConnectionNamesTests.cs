using System.Text;
using Jellyfin.Plugin.Trailhead.Edge;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>The wire reader that finds the names a Connection header nominates, which Kestrel hides from the app.</summary>
public sealed class ConnectionNamesTests
{
    private static ConnectionNames Feed(string wire, int pieceSize)
    {
        var bytes = Encoding.ASCII.GetBytes(wire);
        var names = new ConnectionNames();
        for (var i = 0; i < bytes.Length; i += pieceSize)
        {
            names.Feed(bytes.AsSpan(i, Math.Min(pieceSize, bytes.Length - i)));
        }

        return names;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Each_request_in_a_pipelined_stream_gets_its_own_names_however_the_bytes_arrive(int pieceSize)
    {
        const string wire =
            "POST /a HTTP/1.1\r\nHost: x\r\nConnection: keep-alive, X-A\r\nContent-Length: 30\r\n\r\n"
            + "Connection: X-Fake\r\n\r\nGET / HT"
            + "GET /b HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nConnection: X-B,X-C\r\n\r\n"
            + "5\r\nhello\r\n1a;ext=1\r\nConnection: X-Fake\r\n\r\n1234\r\n0\r\nTrailer: t\r\n\r\n"
            + "\r\nGET /c HTTP/1.1\r\nHost: x\r\n\r\n";

        var names = Feed(wire, pieceSize);

        Assert.Equal(new[] { "keep-alive", "X-A" }, names.Next());
        Assert.Equal(new[] { "X-B", "X-C" }, names.Next());
        Assert.Equal(Array.Empty<string>(), names.Next());
        Assert.Null(names.Next());
        Assert.False(names.Lost);
    }

    [Fact]
    public void An_upgrade_stops_the_reading_without_losing_anything()
    {
        var names = Feed("GET /ws HTTP/1.1\r\nHost: x\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n\u0081\u0085garbage\r\n\r\n", 5);

        Assert.Equal(new[] { "Upgrade" }, names.Next());
        Assert.Null(names.Next());
        Assert.True(names.Upgraded);
        Assert.False(names.Lost);
    }

    [Fact]
    public void Bytes_that_cant_be_followed_are_marked_lost()
    {
        var names = Feed("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n", 4);

        Assert.True(names.Lost);
        Assert.Equal(Array.Empty<string>(), names.Next());
        Assert.Null(names.Next());
    }

    [Fact]
    public void Given_nothing_it_has_no_entry()
    {
        // The empty input: no request, so no names, and nothing made up.
        var names = Feed(string.Empty, 1);

        Assert.Null(names.Next());
        Assert.False(names.Lost);
    }
}

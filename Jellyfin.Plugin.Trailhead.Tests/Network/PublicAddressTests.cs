using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Trailhead.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Network;

/// <summary>
/// A5: each lookup is pinned to one address family. The fake is a dual-stack "what's my IP" service on
/// <c>localhost</c> (127.0.0.1 and ::1, one port) that answers with the family the connection came in on, the way
/// a real one does. An unpinned client asked the IPv4 question gets whichever family it connected over.
/// </summary>
public sealed class PublicAddressTests : IAsyncLifetime
{
    private const string V4Answer = "203.0.113.5";
    private const string V6Answer = "2001:db8::5";

    private WebApplication? _app;
    private int _port;

    /// <summary>What the next answer is, by path, so one server can play several services.</summary>
    private readonly Dictionary<string, Func<HttpContext, string?>> _paths = new();

    public async Task InitializeAsync()
    {
        _paths["/ip"] = ctx => ctx.Connection.LocalIpAddress!.AddressFamily == AddressFamily.InterNetworkV6 ? V6Answer : V4Answer;
        for (var attempt = 0; ; attempt++)
        {
            _port = FreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(_port));
            var app = builder.Build();
            app.Run(async ctx =>
            {
                if (_paths.TryGetValue(ctx.Request.Path, out var answer) && answer(ctx) is { } text)
                {
                    await ctx.Response.WriteAsync(text);
                }
                else
                {
                    ctx.Response.StatusCode = 503;
                }
            });
            try
            {
                await app.StartAsync();
                _app = app;
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await app.DisposeAsync();
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private Uri At(string path) => new("http://localhost:" + _port + path);

    [Fact]
    public async Task Each_question_is_answered_over_its_own_family_on_a_dual_stack_host()
    {
        using var lookup = new PublicAddressLookup(new[] { At("/ip") }, new[] { At("/ip") });

        var found = await lookup.LookupAsync(CancellationToken.None);

        Assert.Equal(IPAddress.Parse(V4Answer), found.V4);
        Assert.Equal(IPAddress.Parse(V6Answer), found.V6);
    }

    [Fact]
    public async Task The_fake_really_is_dual_stack_so_an_unpinned_client_can_get_either_answer()
    {
        // The harness check: localhost resolves to both families here, and the answer follows the connection.
        using var unpinned = new HttpClient();
        using var v4 = PublicAddressLookup.Pinned(AddressFamily.InterNetwork);
        using var v6 = PublicAddressLookup.Pinned(AddressFamily.InterNetworkV6);

        Assert.Equal(V4Answer, await v4.GetStringAsync(At("/ip")));
        Assert.Equal(V6Answer, await v6.GetStringAsync(At("/ip")));
        Assert.Contains(await unpinned.GetStringAsync(At("/ip")), new[] { V4Answer, V6Answer });
    }

    [Fact]
    public async Task A_service_that_is_down_or_answers_a_web_page_falls_through_to_the_next()
    {
        _paths["/page"] = _ => "<html><body>" + new string('x', 400) + "</body></html>";
        using var lookup = new PublicAddressLookup(new[] { At("/down"), At("/page"), At("/ip") }, new[] { At("/down") });

        var found = await lookup.LookupAsync(CancellationToken.None);

        Assert.Equal(IPAddress.Parse(V4Answer), found.V4);
        Assert.Null(found.V6);
    }

    [Fact]
    public async Task An_empty_answer_or_no_services_at_all_is_no_address()
    {
        _paths["/empty"] = _ => string.Empty;
        using var lookup = new PublicAddressLookup(new[] { At("/empty") }, Array.Empty<Uri>());

        var found = await lookup.LookupAsync(CancellationToken.None);

        Assert.Null(found.V4);
        Assert.Null(found.V6);
    }

    [Theory]
    [InlineData("203.0.113.5", AddressFamily.InterNetwork, true)]
    [InlineData("192.168.1.2", AddressFamily.InterNetwork, false)]
    [InlineData("10.0.0.1", AddressFamily.InterNetwork, false)]
    [InlineData("100.64.1.1", AddressFamily.InterNetwork, false)]
    [InlineData("127.0.0.1", AddressFamily.InterNetwork, false)]
    [InlineData("2001:db8::5", AddressFamily.InterNetwork, false)]
    [InlineData("2001:db8::5", AddressFamily.InterNetworkV6, true)]
    [InlineData("fd00::1", AddressFamily.InterNetworkV6, false)]
    [InlineData("fe80::1", AddressFamily.InterNetworkV6, false)]
    [InlineData("::ffff:203.0.113.5", AddressFamily.InterNetworkV6, false)]
    [InlineData("203.0.113.5", AddressFamily.InterNetworkV6, false)]
    [InlineData("hello", AddressFamily.InterNetwork, false)]
    public void Only_a_public_address_of_the_asked_family_counts(string text, AddressFamily family, bool counts)
        => Assert.Equal(counts, PublicAddressLookup.Parse(text + "\n", family) is not null);
}

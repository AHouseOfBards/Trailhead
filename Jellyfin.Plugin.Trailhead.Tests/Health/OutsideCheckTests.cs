using System.Collections.Concurrent;
using Jellyfin.Plugin.Trailhead.Health;
using Jellyfin.Plugin.Trailhead.Tests.Acme;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Health;

/// <summary>A checker that counts what it is asked. It answers <see cref="Answer"/>.</summary>
internal sealed class CountingCheck : IOutsideCheck
{
    internal int Calls;

    internal OutsideResult Answer { get; set; } = new(OutsideState.PortOpen, "connected");

    public Task<OutsideResult> CheckAsync(string host, int port, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(Answer);
    }
}

/// <summary>check-host.net's two routes on a real Kestrel, answering the shapes its API page shows.</summary>
internal sealed class FakeCheckHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeCheckHost(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    internal Uri BaseUrl { get; }

    internal ConcurrentQueue<string> Seen { get; } = new();

    /// <summary>The check-result bodies, one per poll; the last one repeats.</summary>
    internal List<string> Results { get; } = new();

    internal string Start { get; set; } = "{\"ok\":1,\"request_id\":\"806dfb\",\"permanent_link\":\"https://check-host.net/check-report/806dfb\",\"nodes\":{}}";

    private int _polls;

    internal static async Task<FakeCheckHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeCheckHost? fake = null;
        app.Run(ctx => fake!.HandleAsync(ctx));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeCheckHost(app, new Uri(address.TrimEnd('/') + "/"));
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        Seen.Enqueue(ctx.Request.Path + ctx.Request.QueryString);
        ctx.Response.ContentType = "application/json";
        if (ctx.Request.Path.StartsWithSegments("/check-tcp"))
        {
            await ctx.Response.WriteAsync(Start);
            return;
        }

        if (ctx.Request.Path.StartsWithSegments("/check-result"))
        {
            var i = Math.Min(Interlocked.Increment(ref _polls) - 1, Results.Count - 1);
            await ctx.Response.WriteAsync(i < 0 ? "{}" : Results[i]);
            return;
        }

        ctx.Response.StatusCode = 404;
    }
}

public sealed class OutsideCheckTests
{
    [Fact]
    public void The_shipped_switch_is_off_until_the_owner_has_check_hosts_terms_in_writing()
    {
        Assert.False(OutsideCheckSwitch.CheckHostTermsConfirmed);
        Assert.False(new Configuration.PluginConfiguration().OutsideCheckAllowed);
    }

    [Fact]
    public async Task Off_by_default_it_answers_not_checked_and_the_checker_is_never_asked()
    {
        var inner = new CountingCheck();
        var config = new Configuration.PluginConfiguration();
        var check = new OutsideCheckSwitch(inner, () => config.OutsideCheckAllowed);

        var result = await check.CheckAsync("trailhead-test.example.com", 443, CancellationToken.None);

        Assert.Equal(OutsideState.NotChecked, result.State);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task The_admins_setting_alone_does_not_turn_it_on()
    {
        var inner = new CountingCheck();
        var check = new OutsideCheckSwitch(inner, () => true);

        var result = await check.CheckAsync("trailhead-test.example.com", 443, CancellationToken.None);

        Assert.Equal(OutsideState.NotChecked, result.State);
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task With_both_switches_on_the_checker_is_asked()
    {
        // The other half of the two tests above: the counter does see a call when there is one.
        var inner = new CountingCheck();
        var check = new OutsideCheckSwitch(inner, () => true, termsConfirmed: true);

        var result = await check.CheckAsync("trailhead-test.example.com", 443, CancellationToken.None);

        Assert.Equal(OutsideState.PortOpen, result.State);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task The_terms_constant_alone_does_not_turn_it_on_either()
    {
        var inner = new CountingCheck();
        var check = new OutsideCheckSwitch(inner, () => false, termsConfirmed: true);

        Assert.Equal(OutsideState.NotChecked, (await check.CheckAsync("trailhead-test.example.com", 443, CancellationToken.None)).State);
        Assert.Equal(0, inner.Calls);
    }

    private static async Task<OutsideResult> AgainstFakeAsync(FakeCheckHost fake)
    {
        using var client = new CheckHostClient(new HttpClient(), new ListLogger(), baseUrl: fake.BaseUrl, pollEvery: TimeSpan.FromMilliseconds(5), pollFor: TimeSpan.FromMilliseconds(400));
        return await client.CheckAsync("trailhead-test.example.com", 443, CancellationToken.None);
    }

    [Fact]
    public async Task A_node_that_connected_is_port_open_never_working()
    {
        await using var fake = await FakeCheckHost.StartAsync();
        fake.Results.Add("{\"us1.node.check-host.net\":[{\"time\":0.03,\"address\":\"203.0.113.7\"}],\"ch1.node.check-host.net\":[{\"error\":\"Connection timed out\"}],\"pt1.node.check-host.net\":null}");

        var result = await AgainstFakeAsync(fake);

        Assert.Equal(OutsideState.PortOpen, result.State);
        Assert.Contains(fake.Seen, s => s.StartsWith("/check-tcp?host=trailhead-test.example.com%3A443&max_nodes=3", StringComparison.Ordinal));
        Assert.Contains(fake.Seen, s => s == "/check-result/806dfb");
    }

    [Fact]
    public async Task Every_node_failing_is_not_reachable_once_all_have_answered()
    {
        await using var fake = await FakeCheckHost.StartAsync();
        fake.Results.Add("{\"a\":[{\"error\":\"Connection timed out\"}],\"b\":null}");
        fake.Results.Add("{\"a\":[{\"error\":\"Connection timed out\"}],\"b\":[{\"error\":\"Connection refused\"}]}");

        var result = await AgainstFakeAsync(fake);

        Assert.Equal(OutsideState.NotReachable, result.State);
        Assert.Equal("refused", result.Reason);
    }

    [Fact]
    public async Task Nodes_that_never_finish_are_could_not_check()
    {
        await using var fake = await FakeCheckHost.StartAsync();
        fake.Results.Add("{\"a\":null,\"b\":null}");

        Assert.Equal(OutsideState.CouldNotCheck, (await AgainstFakeAsync(fake)).State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"ok\":1,\"request_id\":\"../x\"}")]
    public async Task A_start_answer_without_a_usable_request_id_is_could_not_check(string start)
    {
        await using var fake = await FakeCheckHost.StartAsync();
        fake.Start = start;

        Assert.Equal(OutsideState.CouldNotCheck, (await AgainstFakeAsync(fake)).State);
        Assert.DoesNotContain(fake.Seen, s => s.StartsWith("/check-result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_result_is_never_read_as_a_pass()
    {
        Assert.Null(CheckHostClient.Read(null));
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Null(CheckHostClient.Read(empty));
        await using var fake = await FakeCheckHost.StartAsync();
        Assert.Equal(OutsideState.CouldNotCheck, (await AgainstFakeAsync(fake)).State);
    }
}

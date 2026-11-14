using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

public sealed class AcmeHttpHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private static HttpClient ClientFor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, FakeTimeProvider time, ListLogger log, TimeSpan? timeout, out AcmeHttpHandler handler)
    {
        handler = new AcmeHttpHandler(new StubHandler(respond), log, time, timeout ?? TimeSpan.FromSeconds(30));
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static Task<HttpResponseMessage> Reply(HttpStatusCode code, Action<HttpResponseMessage>? tweak = null)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent("{\"type\":\"urn:ietf:params:acme:error:rateLimited\"}") };
        tweak?.Invoke(response);
        return Task.FromResult(response);
    }

    [Fact]
    public async Task Keeps_a_Retry_After_given_in_seconds()
    {
        using var client = ClientFor((_, _) => Reply(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120))), new FakeTimeProvider(Now), new ListLogger(), null, out var handler);

        using var response = await client.GetAsync("https://acme.example/dir");

        Assert.Equal(TimeSpan.FromSeconds(120), handler.LastRetryAfter);
        // Certes still has to read the error body.
        Assert.Contains("rateLimited", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Keeps_a_Retry_After_given_as_an_HTTP_date()
    {
        var time = new FakeTimeProvider(Now);
        using var client = ClientFor((_, _) => Reply(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(90))), time, new ListLogger(), null, out var handler);

        using var response = await client.GetAsync("https://acme.example/dir");

        Assert.Equal(TimeSpan.FromSeconds(90), handler.LastRetryAfter);
    }

    [Fact]
    public async Task A_Retry_After_date_in_the_past_means_no_wait_and_a_success_clears_it()
    {
        var fail = true;
        using var client = ClientFor((_, _) => fail ? Reply(HttpStatusCode.ServiceUnavailable, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(-30))) : Reply(HttpStatusCode.OK), new FakeTimeProvider(Now), new ListLogger(), null, out var handler);

        using (await client.GetAsync("https://acme.example/dir"))
        {
        }

        Assert.Equal(TimeSpan.Zero, handler.LastRetryAfter);

        fail = false;
        using (await client.GetAsync("https://acme.example/dir"))
        {
        }

        Assert.Null(handler.LastRetryAfter);
    }

    [Fact]
    public async Task A_slow_request_gives_TimeoutException_not_a_cancel()
    {
        var time = new FakeTimeProvider(Now);
        using var client = ClientFor(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        }, time, new ListLogger(), TimeSpan.FromSeconds(10), out _);

        var pending = client.GetAsync("https://acme.example/dir");
        time.Advance(TimeSpan.FromSeconds(11));

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
    }

    [Fact]
    public async Task A_cancel_from_the_caller_stays_a_cancel()
    {
        var time = new FakeTimeProvider(Now);
        using var cts = new CancellationTokenSource();
        using var client = ClientFor(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        }, time, new ListLogger(), TimeSpan.FromSeconds(10), out _);

        var pending = client.GetAsync("https://acme.example/dir", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Logs_the_method_host_and_status_and_nothing_from_the_path_or_query()
    {
        var log = new ListLogger();
        using var client = ClientFor((_, _) => Reply(HttpStatusCode.BadRequest), new FakeTimeProvider(Now), log, null, out _);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://acme.example/acme/new-acct/FAKETOKEN123?apikey=FAKESECRET456") { Content = new StringContent("{\"payload\":\"FAKEBODY789\"}") };
        using var response = await client.SendAsync(request);

        var text = log.All;
        Assert.Contains("POST", text);
        Assert.Contains("acme.example", text);
        Assert.Contains("400", text);
        Assert.DoesNotContain("FAKETOKEN123", text);
        Assert.DoesNotContain("FAKESECRET456", text);
        Assert.DoesNotContain("FAKEBODY789", text);
        Assert.DoesNotContain("new-acct", text);
        Assert.DoesNotContain("rateLimited", text);
    }

    [Fact]
    public async Task Logs_nothing_at_all_about_a_failed_connection_beyond_the_exception_it_rethrows()
    {
        var log = new ListLogger();
        using var client = ClientFor((_, _) => throw new HttpRequestException("connect to https://acme.example/secret-path?token=FAKETOKEN123 failed"), new FakeTimeProvider(Now), log, null, out _);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://acme.example/secret-path?token=FAKETOKEN123"));

        Assert.DoesNotContain("FAKETOKEN123", log.All);
    }

    private static async Task<(WebApplication App, string Url)> StartServerAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/", () => "hello");
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, url.Replace("[::]", "127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trusts_an_extra_CA_only_when_one_is_passed()
    {
        var (server, ca) = TestPki.Server();
        var (_, otherCa) = TestPki.Server();
        var (app, url) = await StartServerAsync(server);
        try
        {
            var (withCa, _) = AcmeHttpHandler.Create(new ListLogger(), TimeProvider.System, TimeSpan.FromSeconds(10), ca);
            var (withOtherCa, _) = AcmeHttpHandler.Create(new ListLogger(), TimeProvider.System, TimeSpan.FromSeconds(10), otherCa);
            var (withNone, _) = AcmeHttpHandler.Create(new ListLogger(), TimeProvider.System, TimeSpan.FromSeconds(10));

            Assert.Equal("hello", await withCa.GetStringAsync(url));
            await Assert.ThrowsAsync<HttpRequestException>(() => withOtherCa.GetStringAsync(url));
            await Assert.ThrowsAsync<HttpRequestException>(() => withNone.GetStringAsync(url));
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}

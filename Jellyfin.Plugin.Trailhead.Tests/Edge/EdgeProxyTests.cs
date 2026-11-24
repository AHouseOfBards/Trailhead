using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Campground.Shared.Contracts;
using Campground.Shared.Registry;
using Jellyfin.Plugin.Trailhead.Edge;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.Tests.Edge;

/// <summary>
/// Edge's request rules, run through the real thing: EdgeService's Kestrel with TLS, YARP, and a stand-in Jellyfin on a
/// real socket with the forwarded-headers middleware Jellyfin uses.
/// </summary>
[Collection(RegistryCollection.Name)]
public sealed class EdgeProxyTests : IAsyncLifetime
{
    private EdgeRig _rig = null!;

    public async Task InitializeAsync()
    {
        _rig = await EdgeRig.StartAsync();
        await _rig.StartEdgeAsync();
        Assert.True(_rig.Service!.Gate.IsOpen, "gate: " + _rig.Service.Gate.Reason + " " + _rig.Log.All);
    }

    public async Task DisposeAsync()
    {
        await _rig.DisposeAsync();
        CampgroundLifetime.ResetForTests();
    }

    [Fact]
    public async Task A_clients_forwarding_headers_are_overwritten_with_its_real_address_https_and_host()
    {
        using var client = _rig.Client("127.0.0.5");
        using var request = new HttpRequestMessage(HttpMethod.Get, "echo");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");
        request.Headers.TryAddWithoutValidation("X-Original-For", "127.0.0.1:1");
        request.Headers.TryAddWithoutValidation("X-Real-IP", "203.0.113.9");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=203.0.113.9");
        request.Headers.Host = "trailhead-test.example.com";

        using var response = await client.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok 127.0.0.5", await response.Content.ReadAsStringAsync());
        var seen = _rig.Jellyfin.Last("/echo");
        Assert.Equal(new[] { "127.0.0.5" }, seen.Headers["X-Forwarded-For"]);
        Assert.Equal(new[] { "https" }, seen.Headers["X-Forwarded-Proto"]);
        Assert.Equal(new[] { "trailhead-test.example.com" }, seen.Headers["X-Forwarded-Host"]);
        Assert.False(seen.Headers.ContainsKey("X-Original-For"));
        Assert.False(seen.Headers.ContainsKey("X-Real-IP"));
        Assert.False(seen.Headers.ContainsKey("Forwarded"));
        Assert.Equal("127.0.0.1", seen.Remote);
        Assert.Equal("127.0.0.5", seen.RemoteAfter);
    }

    [Fact]
    public async Task A_clients_marker_is_dropped_and_this_starts_marker_stamped_once_and_never_sent_back()
    {
        using var client = _rig.Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "echo");
        request.Headers.TryAddWithoutValidation(HttpsClient.EdgeMarkerHeader, "forged");
        request.Headers.TryAddWithoutValidation(EdgeTransformer.ProbeHeader, "forged");

        using var response = await client.Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        var seen = _rig.Jellyfin.Last("/echo");
        Assert.Equal(new[] { _rig.Identity.Marker }, seen.Headers[HttpsClient.EdgeMarkerHeader]);
        Assert.False(seen.Headers.ContainsKey(EdgeTransformer.ProbeHeader));
        var answer = string.Join("\n", response.Headers.Concat(response.Content.Headers).Select(h => h.Key + ": " + string.Join(",", h.Value))) + body;
        Assert.DoesNotContain(_rig.Identity.Marker, answer, StringComparison.Ordinal);
        Assert.DoesNotContain(_rig.Identity.Marker, _rig.Log.All, StringComparison.Ordinal);
        Assert.DoesNotContain(_rig.Identity.ProbeToken, _rig.Log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headers_named_in_Connection_and_the_fixed_hop_by_hop_ones_never_reach_Jellyfin_over_a_raw_socket()
    {
        var reply = await RawTls.SendAsync(
            _rig.Port,
            "GET /echo?raw=1 HTTP/1.1\r\nHost: trailhead-test.example.com\r\nConnection: keep-alive, X-Hop, X-Other\r\nX-Hop: secret\r\nX-Other: also\r\n"
            + "Keep-Alive: timeout=5\r\nProxy-Connection: keep-alive\r\nX-Pass: yes\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", reply, StringComparison.Ordinal);
        var seen = _rig.Jellyfin.Last("/echo?raw=1");
        Assert.Equal(new[] { "yes" }, seen.Headers["X-Pass"]);
        Assert.False(seen.Headers.ContainsKey("X-Hop"), "X-Hop reached Jellyfin");
        Assert.False(seen.Headers.ContainsKey("X-Other"), "X-Other reached Jellyfin");
        Assert.False(seen.Headers.ContainsKey("Keep-Alive"));
        Assert.False(seen.Headers.ContainsKey("Proxy-Connection"));
    }

    [Fact]
    public async Task Two_requests_on_one_connection_each_lose_only_the_headers_they_named()
    {
        var reply = await RawTls.SendAsync(
            _rig.Port,
            "POST /echo?pipe=1 HTTP/1.1\r\nHost: h\r\nConnection: X-One\r\nX-One: 1\r\nX-Two: 2\r\nContent-Length: 5\r\n\r\nhello"
            + "GET /echo?pipe=2 HTTP/1.1\r\nHost: h\r\nConnection: X-Two\r\nX-One: 1\r\nX-Two: 2\r\n\r\n",
            responses: 2);

        Assert.Equal(2, reply.Split("HTTP/1.1 200").Length - 1);
        var first = _rig.Jellyfin.Last("/echo?pipe=1");
        var second = _rig.Jellyfin.Last("/echo?pipe=2");
        Assert.False(first.Headers.ContainsKey("X-One"));
        Assert.Equal(new[] { "2" }, first.Headers["X-Two"]);
        Assert.Equal(5, first.BodyBytes);
        Assert.Equal(new[] { "1" }, second.Headers["X-One"]);
        Assert.False(second.Headers.ContainsKey("X-Two"));
    }

    [Fact]
    public async Task Authorization_cookies_Range_and_the_query_string_go_through_unchanged()
    {
        using var client = _rig.Client();
        const string query = "echo?api_key=abc123&ApiKey=Q%20R&x=%2F";
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Client=\"Test\", Device=\"d\", DeviceId=\"id\", Version=\"1\", Token=\"tok\"");
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", "MediaBrowser Token=\"tok2\"");
        request.Headers.TryAddWithoutValidation("Cookie", "a=1; b=2");
        request.Headers.Range = new RangeHeaderValue(100, 199);

        using var response = await client.Http.SendAsync(request);

        var seen = _rig.Jellyfin.Last("/echo?api_key");
        Assert.Equal("/" + query, seen.PathAndQuery);
        Assert.Equal(request.Headers.GetValues("Authorization").ToArray(), seen.Headers["Authorization"]);
        Assert.Equal(new[] { "MediaBrowser Token=\"tok2\"" }, seen.Headers["X-Emby-Authorization"]);
        Assert.Equal(new[] { "a=1; b=2" }, seen.Headers["Cookie"]);
        Assert.Equal(new[] { "bytes=100-199" }, seen.Headers["Range"]);
    }

    [Fact]
    public async Task A_request_body_goes_through_byte_for_byte()
    {
        using var client = _rig.Client();
        var body = new byte[300_000];
        new Random(7).NextBytes(body);

        using var response = await client.Http.PostAsync("echo", new ByteArrayContent(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.Length, _rig.Jellyfin.Last("/echo").BodyBytes);
    }

    [Fact]
    public async Task A_WebSocket_upgrades_and_echoes_through_Edge()
    {
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        await ws.ConnectAsync(new Uri("wss://127.0.0.1:" + _rig.Port + "/ws"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, r.Count));
        Assert.Equal(new[] { _rig.Identity.Marker }, _rig.Jellyfin.Last("/ws").Headers[HttpsClient.EdgeMarkerHeader]);
    }

    [Fact]
    public async Task HTTP2_is_offered_and_works()
    {
        using var client = _rig.Client(version: HttpVersion.Version20);

        using var response = await client.Http.GetAsync("echo");

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_handshake_sends_the_intermediate_with_the_leaf()
    {
        using var client = _rig.Client();

        using var response = await client.Http.GetAsync("echo");

        var sent = Assert.Single(client.SentChains);
        Assert.Contains(sent, s => s.StartsWith("CN=" + EdgeCerts.IntermediatePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slow_reader_slows_Jellyfins_writes_there_is_no_buffering()
    {
        using var client = _rig.Client();
        using var response = await client.Http.GetAsync("big?bytes=" + (400L * 1024 * 1024), HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[16 * 1024];
        long read = 0;
        var clock = Stopwatch.StartNew();

        // About 1.3 MB/s for 3 s.
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            read += await stream.ReadAsync(buffer);
            await Task.Delay(10);
        }

        var written = Interlocked.Read(ref _rig.Jellyfin.BigWritten);

        // Socket buffers on two hops hold a few MB; buffering would have the whole 400 MB written by now.
        Assert.True(written - read < 24L * 1024 * 1024, $"Jellyfin wrote {written} bytes while the client read {read}");
        Assert.True(read > 0);
    }

    [Fact]
    public async Task A_certificate_swap_keeps_the_open_connection_and_gives_a_new_one_the_new_certificate()
    {
        using var first = _rig.Client();
        using var response = await first.Http.GetAsync("big?bytes=" + (8L * 1024 * 1024), HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        long read = await stream.ReadAsync(buffer);
        var old = Assert.Single(first.Thumbprints);

        _rig.Certificate = EdgeCerts.Make();
        await _rig.Service!.EnsureStartedAsync(CancellationToken.None);
        Assert.NotEqual(old, _rig.Service.Certificates.Current!.Thumbprint);

        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            read += n;
        }

        using var again = await first.Http.GetAsync("echo");
        using var second = _rig.Client();
        using var fresh = await second.Http.GetAsync("echo");

        Assert.Equal(8L * 1024 * 1024, read);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(first.Thumbprints);
        Assert.Equal(_rig.Certificate.Leaf.Thumbprint, Assert.Single(second.Thumbprints));
    }

    [Fact]
    public async Task The_echo_route_isnt_forwarded_for_an_ordinary_client()
    {
        using var client = _rig.Client();
        var before = _rig.Jellyfin.All.Count(s => s.PathAndQuery.StartsWith("/Trailhead", StringComparison.OrdinalIgnoreCase));

        using var response = await client.Http.GetAsync("Trailhead/Edge/Echo?n=abcdefgh12");
        using var cased = await client.Http.GetAsync("trailhead/edge/echo/?n=abcdefgh12");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cased.StatusCode);

        // Only the self-test's own requests (from the start) ever reached it.
        Assert.True(before > 0);
        Assert.Equal(before, _rig.Jellyfin.All.Count(s => s.PathAndQuery.StartsWith("/Trailhead", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task The_echo_answers_404_straight_to_Jellyfin_without_the_marker()
    {
        using var direct = new HttpClient();

        using var none = await direct.GetAsync("http://127.0.0.1:" + _rig.Jellyfin.Port + "/Trailhead/Edge/Echo?n=abcdefgh12");
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + _rig.Jellyfin.Port + "/Trailhead/Edge/Echo?n=abcdefgh12");
        wrong.Headers.TryAddWithoutValidation(HttpsClient.EdgeMarkerHeader, "not-the-marker");
        using var wrongAnswer = await direct.SendAsync(wrong);

        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongAnswer.StatusCode);
    }

    [Fact]
    public async Task Status_says_active_unbuffered_with_the_forwarding_address_and_marker_once_the_gate_is_open()
    {
        var status = await HttpsClient.StatusAsync();

        Assert.NotNull(status);
        Assert.True(status!.Configured);
        Assert.Equal("https://trailhead-test.example.com", status.PublicUrl);
        Assert.Equal(new EdgeStatus(true, false, "127.0.0.1", _rig.Identity.Marker), status.Edge);
    }

    [Fact]
    public async Task Status_withholds_the_marker_while_the_gate_is_shut()
    {
        _rig.Forwarding = IPAddress.Parse("192.0.2.10");
        await _rig.Service!.CheckGateAsync(CancellationToken.None);

        var status = await _rig.Service.StatusAsync(CancellationToken.None);

        Assert.False(status!.Edge!.Active);
        Assert.Null(status.Edge.Marker);
    }

    [Fact]
    public void The_framework_logs_are_kept_to_warnings_and_Trailheads_own_are_not()
    {
        Assert.True(EdgeLoggerFactory.IsQuiet("Yarp.ReverseProxy.Forwarder.HttpForwarder"));
        Assert.True(EdgeLoggerFactory.IsQuiet("Microsoft.AspNetCore.Server.Kestrel"));
        Assert.True(EdgeLoggerFactory.IsQuiet("Microsoft.Extensions.Hosting.Internal.Host"));
        Assert.False(EdgeLoggerFactory.IsQuiet("Jellyfin.Plugin.Trailhead.Edge.EdgeService"));
        Assert.False(EdgeLoggerFactory.IsQuiet("YarpLike"));
    }
}

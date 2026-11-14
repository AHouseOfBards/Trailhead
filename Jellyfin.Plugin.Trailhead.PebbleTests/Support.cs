using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Certes;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Trailhead.PebbleTests;

internal sealed class TempFolder : IDisposable
{
    internal TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "trailhead-pebble-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class ListLogger : ILogger
{
    private readonly List<string> _entries = new();

    internal string All
    {
        get
        {
            lock (_entries)
            {
                return string.Join("\n", _entries);
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(logLevel + ": " + formatter(state, exception));
        }
    }
}

/// <summary>DNS-01 against pebble-challtestsrv: its management API sets and clears TXT records, and Pebble reads them over DNS.</summary>
internal sealed class ChallTestSrvDns : IDnsChallenge
{
    private static readonly HttpClient Http = new();
    private readonly PebbleEnv _env;
    private readonly List<string> _set = new();

    internal ChallTestSrvDns(PebbleEnv env) => _env = env;

    /// <summary>Tests flip this to publish a value other than the one the CA asked for.</summary>
    internal Func<string, string> Mutate { get; set; } = v => v;

    internal IReadOnlyList<string> HostsSet
    {
        get
        {
            lock (_set)
            {
                return _set.ToList();
            }
        }
    }

    public async Task<string> SetTxtAsync(string name, string value, CancellationToken ct)
    {
        await PostAsync("set-txt", new { host = name, value = Mutate(value) }, ct).ConfigureAwait(false);
        lock (_set)
        {
            _set.Add(name);
        }

        return name;
    }

    public Task RemoveAsync(string handle, CancellationToken ct) => PostAsync("clear-txt", new { host = handle }, ct);

    // set-txt answers once the record is in the server's table, which is also where Pebble reads it, so there is
    // nothing left to wait for. (The Dynu one has to ask every nameserver.)
    public Task WaitUntilVisibleAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;

    /// <summary>How many TXT records the DNS server answers for the name, asked over DNS like Pebble does.</summary>
    internal async Task<int> TxtCountAsync(string host)
    {
        var query = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }

        query.AddRange(new byte[] { 0, 0, 16, 0, 1 });

        using var udp = new UdpClient();
        udp.Connect(_env.DnsHost, _env.DnsPort);
        await udp.SendAsync(query.ToArray()).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var answer = (await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false)).Buffer;
        if (answer.Length < 12 || (answer[3] & 0x0F) != 0)
        {
            throw new InvalidOperationException("The DNS server did not answer the TXT query cleanly.");
        }

        return (answer[6] << 8) | answer[7];
    }

    private async Task PostAsync(string path, object body, CancellationToken ct)
    {
        using var response = await Http.PostAsync(new Uri(_env.ChallengeApi, path), new StringContent(JsonSerializer.Serialize(body)), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Payload);

/// <summary>Sits outside the plugin's own handler and notes what Certes sends, so a test can see the order carry `replaces`.</summary>
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly List<RecordedRequest> _requests = new();

    internal RecordingHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    internal Action<HttpRequestMessage, HttpResponseMessage>? OnResponse { get; set; }

    internal IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? payload = null;
        if (request.Content is not null)
        {
            // ACME bodies are JWS: the payload is base64url JSON.
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var jws = JsonDocument.Parse(body);
                var encoded = jws.RootElement.GetProperty("payload").GetString()!;
                payload = Encoding.UTF8.GetString(Base64Url(encoded));
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
            }
        }

        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, payload));
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        OnResponse?.Invoke(request, response);
        return response;
    }

    private static byte[] Base64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64 + new string('=', (4 - (b64.Length % 4)) % 4));
    }
}

/// <summary>The plugin's own ACME parts wired to Pebble, as the real plugin will wire them to Let's Encrypt.</summary>
internal sealed class Rig : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly HttpClient _client;

    internal Rig(PebbleEnv env, bool nonceReject = false, TimeProvider? time = null, int budgetLimit = 3)
    {
        Env = env;
        Time = time ?? TimeProvider.System;
        Name = "th" + Guid.NewGuid().ToString("N")[..12] + ".example.com";
        Directory = nonceReject ? env.NonceRejectDirectory : env.Directory;
        Management = nonceReject ? env.NonceRejectManagement : env.Management;

        var (acmeClient, handler) = AcmeHttpHandler.Create(Log, Time, TimeSpan.FromSeconds(30), env.LoadCa());

        // Pebble's management API answers 400 "malformed" to a request with no User-Agent.
        _client = new HttpClient(Recorder = new RecordingHandler(handler), disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("trailhead-pebble-tests/1.0");
        AcmeClient = acmeClient;

        Dns = new ChallTestSrvDns(env);
        Store = new CertificateStore(_folder.File("certs"), Time, Log);
        Budget = new FailureBudget(_folder.File("budget.json"), Time, budgetLimit);

        // 5 retries here would leave a Pebble that rejects half the nonces failing a run about 1 time in 8 by luck;
        // 20 makes that vanish, and the test is about newAccount's retry, which is ours, not Certes's.
        var ctx = CertificateIssuer.NewContext(Directory, KeyFactory.NewKey(KeyAlgorithm.ES256), _client, nonceReject ? 20 : 5);
        Issuer = new CertificateIssuer(
            ctx,
            Dns,
            Time,
            Log,
            () => handler.LastRetryAfter,
            new IssuerOptions { PollInterval = TimeSpan.FromMilliseconds(200), MaxPolls = 100, NewAccountAttempts = nonceReject ? 20 : 5 });
    }

    internal PebbleEnv Env { get; }

    internal Uri Directory { get; }

    internal Uri Management { get; }

    internal TimeProvider Time { get; }

    internal string Name { get; }

    internal ListLogger Log { get; } = new();

    internal RecordingHandler Recorder { get; }

    internal HttpClient AcmeClient { get; }

    internal ChallTestSrvDns Dns { get; }

    internal CertificateStore Store { get; }

    internal FailureBudget Budget { get; }

    internal CertificateIssuer Issuer { get; }

    internal string ChallengeHost => "_acme-challenge." + Name;

    /// <summary>The common name of Pebble's root, which the chain it serves ends at (the root itself isn't in the chain).</summary>
    internal async Task<string> RootCommonNameAsync()
    {
        var pem = await _client.GetStringAsync(new Uri(Management, "/roots/0")).ConfigureAwait(false);
        using var root = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
        return root.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
    }

    internal async Task<CertificateManager> NewManagerAsync(string? expectedRoot = null)
    {
        var root = expectedRoot ?? await RootCommonNameAsync().ConfigureAwait(false);

        // "default" is the profile Pebble offers; Let's Encrypt's "classic" isn't in its config.
        var options = new ManagerOptions(Name, "default", null, new CertificateExpectation(Name, root));
        return new CertificateManager(Store, Issuer, Budget, Time, Log, options);
    }

    internal async Task PostManagementAsync(string path, object body)
    {
        using var response = await _client.PostAsync(new Uri(Management, path), new StringContent(JsonSerializer.Serialize(body))).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        AcmeClient.Dispose();
        _folder.Dispose();
    }
}

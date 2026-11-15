using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.TestSupport;

internal sealed record FakeDynuRecord(long Id, long DomainId, string DomainName, string NodeName, string Text, int Ttl);

internal sealed record FakeDynuRequest(string Method, string Path, string? ApiKey, string? Accept, string Body);

/// <summary>A scripted answer, used instead of the normal handling by the next request that arrives.</summary>
/// <param name="StallBody">Send the headers and a Content-Length, then never send the body.</param>
internal sealed record FakeDynuCanned(int Http, string Body, bool StallBody = false);

internal enum DeleteMissingAs
{
    /// <summary>HTTP 200 with a 501 envelope, which is what RASA's live runs saw for an id Dynu doesn't know.</summary>
    Envelope501,
    Http404,
    Http501,
}

/// <summary>
/// A Dynu v2 API on a real Kestrel socket (127.0.0.1, any free port), so the client's own HttpClient path runs.
/// It keeps domains and records in memory and checks the API-Key header. The key and the hostname token are
/// obviously fake, and tests assert that neither ever shows up in a log line or an exception message.
/// </summary>
internal sealed class FakeDynu : IAsyncDisposable
{
    internal const string Key = "FAKE-DYNU-KEY-do-not-use-0000";
    internal const string Token = "FAKE-HOSTNAME-TOKEN-do-not-use-1111";

    private readonly WebApplication _app;
    private readonly object _gate = new();
    private readonly List<(long Id, string Name)> _domains = new();
    private readonly List<FakeDynuRecord> _records = new();
    private readonly List<FakeDynuRequest> _requests = new();
    private readonly Queue<FakeDynuCanned> _canned = new();
    private readonly Queue<FakeDynuCanned> _createScript = new();
    private readonly HashSet<string> _foreign = new();
    private long _nextDomain = 1000;
    private long _nextRecord = 5000;

    private FakeDynu(WebApplication app, Uri baseUrl, string requiredKey)
    {
        _app = app;
        BaseUrl = baseUrl;
        RequiredKey = requiredKey;
    }

    internal Uri BaseUrl { get; }

    internal string RequiredKey { get; }

    internal DeleteMissingAs DeleteMissing { get; set; } = DeleteMissingAs.Envelope501;

    /// <summary>Runs after a TXT record is stored, before the answer is sent (the Pebble tests write it through to challtestsrv here).</summary>
    internal Func<FakeDynuRecord, Task>? OnTxtAdded { get; set; }

    internal Func<FakeDynuRecord, Task>? OnTxtRemoved { get; set; }

    internal IReadOnlyList<FakeDynuRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToList();
            }
        }
    }

    internal IReadOnlyList<FakeDynuRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    internal static async Task<FakeDynu> StartAsync(string requiredKey = Key)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeDynu? fake = null;
        app.Run(ctx => fake!.HandleAsync(ctx));
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        fake = new FakeDynu(app, new Uri(address.TrimEnd('/') + "/v2/"), requiredKey);
        return fake;
    }

    internal long AddDomain(string name)
    {
        lock (_gate)
        {
            var id = _nextDomain++;
            _domains.Add((id, name));
            return id;
        }
    }

    /// <summary>A name that belongs to another account: it is never listed, and creating it fails with 505.</summary>
    internal void AddForeignDomain(string name)
    {
        lock (_gate)
        {
            _foreign.Add(name.ToLowerInvariant());
        }
    }

    /// <summary>Scripted answers for POST /v2/dns only (the first call of a claim is a list, so Enqueue can't aim at the create).</summary>
    internal void EnqueueCreate(FakeDynuCanned canned)
    {
        lock (_gate)
        {
            _createScript.Enqueue(canned);
        }
    }

    internal IReadOnlyList<string> DomainNames
    {
        get
        {
            lock (_gate)
            {
                return _domains.Select(d => d.Name).ToList();
            }
        }
    }

    internal void Enqueue(FakeDynuCanned canned)
    {
        lock (_gate)
        {
            _canned.Enqueue(canned);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    internal static string Envelope(int status, string type = "", string message = "")
        => JsonSerializer.Serialize(new { statusCode = status, type, message });

    private static async Task WriteAsync(HttpContext ctx, int http, string body)
    {
        ctx.Response.StatusCode = http;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body).ConfigureAwait(false);
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var path = ctx.Request.Path.Value ?? string.Empty;
        FakeDynuCanned? canned = null;
        lock (_gate)
        {
            _requests.Add(new FakeDynuRequest(ctx.Request.Method, path, ctx.Request.Headers["API-Key"].FirstOrDefault(), ctx.Request.Headers.Accept.FirstOrDefault(), body));
            if (_canned.Count > 0)
            {
                canned = _canned.Dequeue();
            }
        }

        if (canned is not null)
        {
            if (canned.StallBody)
            {
                ctx.Response.StatusCode = canned.Http;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength = 1000;
                await ctx.Response.StartAsync().ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync().ConfigureAwait(false);
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return;
            }

            await WriteAsync(ctx, canned.Http, canned.Body).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(ctx.Request.Headers["API-Key"].FirstOrDefault(), RequiredKey, StringComparison.Ordinal))
        {
            await WriteAsync(ctx, 401, Envelope(401, "Authentication Exception", "API-Key is not valid.")).ConfigureAwait(false);
            return;
        }

        var parts = path.Trim('/').Split('/');
        if (parts.Length >= 2 && parts[0] == "v2" && parts[1] == "dns")
        {
            if (parts.Length == 2 && ctx.Request.Method == "GET")
            {
                await ListAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (parts.Length == 2 && ctx.Request.Method == "POST")
            {
                await CreateDomainAsync(ctx, body).ConfigureAwait(false);
                return;
            }

            if (parts.Length == 4 && parts[2] == "getroot" && ctx.Request.Method == "GET")
            {
                await GetRootAsync(ctx, Uri.UnescapeDataString(parts[3])).ConfigureAwait(false);
                return;
            }

            if (parts.Length == 4 && parts[3] == "record" && ctx.Request.Method == "POST" && long.TryParse(parts[2], out var domainId))
            {
                await AddRecordAsync(ctx, domainId, body).ConfigureAwait(false);
                return;
            }

            if (parts.Length == 5 && parts[3] == "record" && ctx.Request.Method == "DELETE" && long.TryParse(parts[2], out var d) && long.TryParse(parts[4], out var r))
            {
                await DeleteRecordAsync(ctx, d, r).ConfigureAwait(false);
                return;
            }
        }

        await WriteAsync(ctx, 404, Envelope(404, "Not Found")).ConfigureAwait(false);
    }

    private async Task CreateDomainAsync(HttpContext ctx, string body)
    {
        FakeDynuCanned? scripted = null;
        lock (_gate)
        {
            if (_createScript.Count > 0)
            {
                scripted = _createScript.Dequeue();
            }
        }

        if (scripted is not null)
        {
            await WriteAsync(ctx, scripted.Http, scripted.Body).ConfigureAwait(false);
            return;
        }

        string? name = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            name = doc.RootElement.GetProperty("name").GetString()?.ToLowerInvariant();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        if (string.IsNullOrEmpty(name))
        {
            await WriteAsync(ctx, 200, Envelope(501, "Argument Exception", "Argument is invalid.")).ConfigureAwait(false);
            return;
        }

        bool exists;
        lock (_gate)
        {
            exists = _foreign.Contains(name) || _domains.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                _domains.Add((_nextDomain++, name));
            }
        }

        // 505 is what the old app saw live for a name that already exists.
        await WriteAsync(ctx, 200, exists ? Envelope(505, "Validation Exception", "Hostname is taken.") : Envelope(200)).ConfigureAwait(false);
    }

    private async Task GetRootAsync(HttpContext ctx, string host)
    {
        long? id = null;
        var known = false;
        lock (_gate)
        {
            var own = _domains.Where(d => d.Name.Equals(host, StringComparison.OrdinalIgnoreCase)).Select(d => (long?)d.Id).FirstOrDefault();
            id = own;
            known = own is not null || _foreign.Contains(host.ToLowerInvariant());
        }

        if (!known)
        {
            await WriteAsync(ctx, 200, Envelope(501, "Argument Exception", "Argument is invalid.")).ConfigureAwait(false);
            return;
        }

        await WriteAsync(ctx, 200, JsonSerializer.Serialize(new { statusCode = 200, id = id ?? 0, domainName = host, hostname = host, node = string.Empty })).ConfigureAwait(false);
    }

    private async Task ListAsync(HttpContext ctx)
    {
        object[] domains;
        lock (_gate)
        {
            // The update token comes with every listed hostname, as in the real API.
            domains = _domains.Select(d => (object)new { id = d.Id, name = d.Name, token = Token, ipv4 = true, ipv4Address = "192.0.2.1" }).ToArray();
        }

        await WriteAsync(ctx, 200, JsonSerializer.Serialize(new { statusCode = 200, domains })).ConfigureAwait(false);
    }

    private async Task AddRecordAsync(HttpContext ctx, long domainId, string body)
    {
        string? domainName;
        lock (_gate)
        {
            domainName = _domains.Where(d => d.Id == domainId).Select(d => d.Name).FirstOrDefault();
        }

        string? node = null;
        string? text = null;
        var ttl = 0;
        var state = false;
        var type = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            node = root.GetProperty("nodeName").GetString();
            type = root.GetProperty("recordType").GetString() ?? string.Empty;
            text = root.GetProperty("textData").GetString();
            ttl = root.GetProperty("ttl").GetInt32();
            state = root.GetProperty("state").GetBoolean();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
        }

        if (domainName is null || node is null || type != "TXT" || string.IsNullOrEmpty(text) || ttl <= 0 || !state)
        {
            await WriteAsync(ctx, 200, Envelope(501, "Argument Exception", "Argument is invalid.")).ConfigureAwait(false);
            return;
        }

        FakeDynuRecord record;
        lock (_gate)
        {
            record = new FakeDynuRecord(_nextRecord++, domainId, domainName, node, text, ttl);
            _records.Add(record);
        }

        if (OnTxtAdded is { } added)
        {
            await added(record).ConfigureAwait(false);
        }

        await WriteAsync(ctx, 200, JsonSerializer.Serialize(new
        {
            statusCode = 200,
            id = record.Id,
            domainId,
            domainName,
            nodeName = node,
            recordType = "TXT",
            ttl,
            state = true,
            textData = text,
        })).ConfigureAwait(false);
    }

    private async Task DeleteRecordAsync(HttpContext ctx, long domainId, long recordId)
    {
        FakeDynuRecord? found;
        lock (_gate)
        {
            found = _records.FirstOrDefault(r => r.Id == recordId && r.DomainId == domainId);
            if (found is not null)
            {
                _records.Remove(found);
            }
        }

        if (found is null)
        {
            switch (DeleteMissing)
            {
                case DeleteMissingAs.Http404:
                    await WriteAsync(ctx, 404, Envelope(404, "Not Found")).ConfigureAwait(false);
                    break;
                case DeleteMissingAs.Http501:
                    await WriteAsync(ctx, 501, Envelope(501, "Argument Exception", "Argument is invalid.")).ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(ctx, 200, Envelope(501, "Argument Exception", "Argument is invalid.")).ConfigureAwait(false);
                    break;
            }

            return;
        }

        if (OnTxtRemoved is { } removed)
        {
            await removed(found).ConfigureAwait(false);
        }

        await WriteAsync(ctx, 200, Envelope(200)).ConfigureAwait(false);
    }
}

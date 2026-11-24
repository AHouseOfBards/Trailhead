using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Trailhead.Acme;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Dns;

internal sealed record DynuDomain(long Id, string Name);

internal sealed record DynuCreateResult(DynuDomain Domain, bool Created);

/// <summary>A name's writable fields as Dynu held them when read. Never holds the update token.</summary>
internal sealed class DynuHeldRecord
{
    /// <summary>The writable fields of Dynu's <c>DNS.domain</c> schema (its OpenAPI document; the rest are read-only).</summary>
    internal static readonly IReadOnlyList<string> Writable = new[]
    {
        "name", "group", "ipv4Address", "ipv6Address", "ttl", "ipv4", "ipv6", "ipv4WildcardAlias", "ipv6WildcardAlias", "allowZoneTransfer", "dnssec",
    };

    internal DynuHeldRecord(long id, JsonObject fields)
    {
        Id = id;
        Fields = fields;
    }

    internal long Id { get; }

    internal JsonObject Fields { get; }

    internal string Name => Fields["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    /// <summary>The A record's address when the IPv4 switch is on; null when it is off or empty.</summary>
    internal IPAddress? PublishedIPv4
    {
        get
        {
            if (Fields["ipv4"] is not JsonValue on || !on.TryGetValue<bool>(out var enabled) || !enabled)
            {
                return null;
            }

            var text = Fields["ipv4Address"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            return IPAddress.TryParse(text, out var a) && a.AddressFamily == AddressFamily.InterNetwork ? a : null;
        }
    }
}

internal sealed class DynuClientOptions
{
    internal int MaxAttempts { get; init; } = 3;

    internal TimeSpan BackoffBase { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>For the whole attempt, headers and body.</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal int MaxBodyBytes { get; init; } = 4 * 1024 * 1024;
}

/// <summary>
/// The parts of Dynu's v2 REST API that DNS-01 needs. It logs the method, the path (ids only), the status and the
/// attempt, and nothing else: list responses carry a per-hostname update token, which is a credential, and the key
/// is never written anywhere.
/// </summary>
internal sealed class DynuClient : IDisposable
{
    internal static readonly Uri DefaultBase = new("https://api.dynu.com/v2/");

    // Short: a challenge record only has to live until the CA has looked at it.
    private const int Ttl = 120;

    private const int DomainTtl = 90;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _apiKey;
    private readonly Uri _base;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly DynuClientOptions _options;

    internal DynuClient(HttpClient http, string apiKey, ILogger logger, TimeProvider time, Uri? baseUrl = null, DynuClientOptions? options = null, bool ownsHttp = false)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("The Dynu key is empty.", nameof(apiKey));
        }

        _http = http;
        _ownsHttp = ownsHttp;
        _apiKey = apiKey.Trim();
        _logger = logger;
        _time = time;
        _options = options ?? new DynuClientOptions();
        var b = baseUrl ?? DefaultBase;
        _base = b.AbsoluteUri.EndsWith('/') ? b : new Uri(b.AbsoluteUri + "/");
    }

    internal static DynuClient Create(string apiKey, ILogger logger, TimeProvider time, Uri? baseUrl = null, DynuClientOptions? options = null)
    {
        // No redirects: the handler would send the API-Key header on to wherever Dynu (or something in the way) points.
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = false };

        // Our own timeout, through TimeProvider; HttpClient's would report a plain cancel.
        var http = new HttpClient(sockets, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new DynuClient(http, apiKey, logger, time, baseUrl, options, ownsHttp: true);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    internal async Task<IReadOnlyList<DynuDomain>> ListDomainsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "dns", null, ct).ConfigureAwait(false);
        var result = new List<DynuDomain>();
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("domains", out var domains) && domains.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in domains.EnumerateArray())
            {
                // Only the id and the name are read. The update token in the same object is never copied anywhere.
                if (d.ValueKind == JsonValueKind.Object
                    && d.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var idValue)
                    && d.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } nameValue)
                {
                    result.Add(new DynuDomain(idValue, nameValue));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Claims a hostname. A name already on the account comes back as it is (Created false), so a re-run makes no second
    /// one. POST dns answers with a status only, so the id is found afterwards in the list.
    /// </summary>
    internal async Task<DynuCreateResult> CreateDomainAsync(string name, CancellationToken ct)
    {
        var existing = Find(await ListDomainsAsync(ct).ConfigureAwait(false), name);
        if (existing is not null)
        {
            return new DynuCreateResult(existing, Created: false);
        }

        // No address: publishing one is the address sync's job, and no AAAA goes out until it has been tested (T12).
        var body = new { name, ttl = DomainTtl, ipv4 = false, ipv6 = false };
        using (await SendAsync(HttpMethod.Post, "dns", body, ct).ConfigureAwait(false))
        {
        }

        var created = Find(await ListDomainsAsync(ct).ConfigureAwait(false), name);
        if (created is null)
        {
            // The list may lag the create; the next run finds it through the check above.
            throw new DynuException(FailureKind.Transient, "Dynu accepted the new name, but it is not in the account's list yet.");
        }

        return new DynuCreateResult(created, Created: true);
    }

    private static DynuDomain? Find(IReadOnlyList<DynuDomain> domains, string name)
        => domains.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a TXT record and returns its id.</summary>
    internal async Task<long> AddTxtAsync(long domainId, string nodeName, string text, CancellationToken ct)
    {
        RequireId(domainId, "domain");
        var body = new { nodeName, recordType = "TXT", textData = text, ttl = Ttl, state = true };
        using var doc = await SendAsync(HttpMethod.Post, $"dns/{domainId}/record", body, ct).ConfigureAwait(false);
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var recordId) && recordId > 0)
        {
            return recordId;
        }

        // The record may exist now, but without its id nothing can remove it; say so rather than carry on with 0.
        throw new DynuException(FailureKind.Permanent, $"Dynu POST dns/{domainId}/record gave no record id.");
    }

    /// <summary>
    /// Deletes a record. One that is already gone counts as deleted (404, or Dynu's 501), so cleanup is safe to repeat.
    /// An id of 0 or less is refused first: Dynu answers a malformed id with the same 501, so without this check a
    /// delete of 0 would report success and remove nothing.
    /// </summary>
    internal async Task DeleteRecordAsync(long domainId, long recordId, CancellationToken ct)
    {
        RequireId(domainId, "domain");
        RequireId(recordId, "record");
        try
        {
            using var doc = await SendAsync(HttpMethod.Delete, $"dns/{domainId}/record/{recordId}", null, ct).ConfigureAwait(false);
        }
        catch (DynuException e) when (e.Status is 404 or 501)
        {
            _logger.LogInformation("Dynu record {RecordId} on domain {DomainId} was already gone.", recordId, domainId);
        }
    }

    /// <summary>
    /// The name's record as Dynu holds it now (<c>GET /v2/dns/{id}</c>), not a copy kept here (A3). Only the fields
    /// Dynu's schema lets a caller write are kept, so the update can send them back as they were; the per-hostname
    /// token in the same answer is never copied.
    /// </summary>
    internal async Task<DynuHeldRecord> GetDomainAsync(long domainId, CancellationToken ct)
    {
        RequireId(domainId, "domain");
        using var doc = await SendAsync(HttpMethod.Get, $"dns/{domainId}", null, ct).ConfigureAwait(false);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new DynuException(FailureKind.Transient, $"Dynu GET dns/{domainId} gave no record.");
        }

        var root = doc.RootElement;
        var held = new JsonObject();
        foreach (var field in DynuHeldRecord.Writable)
        {
            if (root.TryGetProperty(field, out var value))
            {
                held[field] = JsonNode.Parse(value.GetRawText());
            }
        }

        // Without the name and both switches there is nothing safe to send back: a field left out may be reset.
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("ipv4", out var v4) || v4.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("ipv6", out var v6) || v6.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new DynuException(FailureKind.Permanent, $"Dynu GET dns/{domainId} gave a record without its name or address switches.");
        }

        return new DynuHeldRecord(domainId, held);
    }

    /// <summary>
    /// Points the name's A record at <paramref name="ipv4"/> (<c>POST /v2/dns/{id}</c>: Dynu's OpenAPI document,
    /// operation <c>dnsIdPost</c>, body <c>DNS.domain</c>). Every other field goes back exactly as
    /// <see cref="GetDomainAsync"/> read it, the AAAA switch and address too, so an AAAA the admin set stays and
    /// Trailhead never sends an IPv6 address of its own (T12, plan-finish choice 2). The schema doesn't say whether a
    /// field left out is kept or reset, which is why nothing is left out.
    /// UNVERIFIED LIVE: built from Dynu's OpenAPI document and FakeDynu (plan-finish choice 7). Check it against a real
    /// account before release.
    /// </summary>
    internal async Task UpdateIPv4Async(DynuHeldRecord held, IPAddress ipv4, CancellationToken ct)
    {
        RequireId(held.Id, "domain");
        if (ipv4.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new DynuException(FailureKind.Permanent, "Refusing to send Dynu an address that isn't IPv4.");
        }

        var body = (JsonObject)held.Fields.DeepClone();
        body["ipv4"] = true;
        body["ipv4Address"] = ipv4.ToString();
        using (await SendAsync(HttpMethod.Post, $"dns/{held.Id}", body, ct).ConfigureAwait(false))
        {
        }
    }

    /// <summary>True when Dynu accepts the key, false when it says 401 or 403. Anything else (network, 5xx) throws.</summary>
    internal async Task<bool> CheckKeyAsync(CancellationToken ct)
    {
        try
        {
            await ListDomainsAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DynuException e) when (e.AuthRejected)
        {
            return false;
        }
    }

    private static void RequireId(long id, string what)
    {
        if (id <= 0)
        {
            throw new DynuException(FailureKind.Permanent, $"Refusing a Dynu call with an invalid {what} id ({id}).");
        }
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            DynuException error;
            try
            {
                return await AttemptAsync(method, path, body, attempt, ct).ConfigureAwait(false);
            }
            catch (DynuException e) when (e.Kind == FailureKind.Transient)
            {
                error = e;
            }

            if (attempt >= _options.MaxAttempts)
            {
                throw new DynuException(FailureKind.Transient, error.Message + $" Gave up after {attempt} attempts.", error.Status, fromEnvelope: error.FromEnvelope, errorType: error.ErrorType);
            }

            // Exponential with jitter: hammering a rate limit is how an account gets throttled harder.
            var wait = TimeSpan.FromTicks(_options.BackoffBase.Ticks * (1L << (attempt - 1))) + TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * _options.BackoffBase.Ticks));
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument?> AttemptAsync(HttpMethod method, string path, object? body, int attempt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.Add("API-Key", _apiKey);
        request.Headers.Add("Accept", "application/json");
        if (body is not null)
        {
            // StringContent, not JsonContent: JsonContent streams the body with no Content-Length, and live Dynu
            // answers that with 501 "Invalid." for every create (2026-11-16). FakeDynu refuses it the same way.
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var timeout = new CancellationTokenSource(_options.RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        byte[] raw;
        int status;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            raw = await ReadLimitedAsync(response, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            Log(method, path, 0, attempt);
            throw new DynuException(FailureKind.Transient, $"Dynu {method} {path} took longer than {_options.RequestTimeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException)
        {
            // Its message can carry the host and an inner socket error; the type of failure is all anyone needs.
            Log(method, path, 0, attempt);
            throw new DynuException(FailureKind.Transient, $"Dynu {method} {path} could not be reached.");
        }

        JsonDocument? doc = null;
        if (raw.Length > 0)
        {
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                // Not JSON: a proxy's page, most likely. Handled below by status.
            }
        }

        try
        {
            var (envelopeStatus, envelopeType) = ReadEnvelope(doc);
            var effective = envelopeStatus ?? status;
            var fromEnvelope = envelopeStatus is not null;
            Log(method, path, effective, attempt);

            // Dynu also reports failures inside a 200, so the envelope is checked on every response.
            var failed = status is < 200 or >= 300 || (envelopeStatus is { } s && (s < 200 || s >= 300));
            if (failed)
            {
                throw Failure(method, path, effective, fromEnvelope, envelopeType);
            }

            if (raw.Length > 0 && doc is null)
            {
                throw new DynuException(FailureKind.Transient, $"Dynu {method} {path} answered with something that is not JSON.", status);
            }

            var result = doc;
            doc = null;
            return result;
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _options.MaxBodyBytes)
            {
                // The same body would come again, so a retry can't help.
                throw new DynuException(FailureKind.Permanent, $"A Dynu response was larger than {_options.MaxBodyBytes} bytes.", (int)response.StatusCode);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private (int? Status, string? Type) ReadEnvelope(JsonDocument? doc)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("statusCode", out var code)
            || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var value) || value == 0)
        {
            return (null, null);
        }

        return (value, Text(doc.RootElement, "type"));
    }

    private string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // Only the error's category ("Argument Exception"), and only if it looks like one. Dynu's free-text message is
        // left out: it is server-controlled, and an exception message must never be able to carry a secret.
        var text = p.GetString()!;
        return text.Length is > 0 and <= 60 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ') && !text.Contains(_apiKey, StringComparison.Ordinal) ? text : null;
    }

    private static DynuException Failure(HttpMethod method, string path, int status, bool fromEnvelope, string? type)
    {
        var what = type ?? string.Empty;
        var text = $"Dynu {method} {path} failed with {status}" + (what.Length > 0 ? ": " + what : ".");

        if (status is 401 or 403)
        {
            return new DynuException(FailureKind.Permanent, $"Dynu {method} {path} refused the API key ({status}).", status, authRejected: true);
        }

        // These describe the request, so the same request gets the same answer; retrying only delays the real message.
        // 501 and 505 are Dynu's; 502 and 503 mean something else on a gateway, so they count only from the envelope.
        var application = status is 501 or 505 || (fromEnvelope && status is 502 or 503);
        if (application)
        {
            return new DynuException(FailureKind.Permanent, text, status, fromEnvelope: fromEnvelope, errorType: type);
        }

        if (status == 429 || status >= 500)
        {
            return new DynuException(FailureKind.Transient, text, status, fromEnvelope: fromEnvelope, errorType: type);
        }

        return new DynuException(FailureKind.Permanent, text, status, fromEnvelope: fromEnvelope, errorType: type);
    }

    private void Log(HttpMethod method, string path, int status, int attempt)
        => _logger.LogInformation("Dynu {Method} {Path} -> {Status} (attempt {Attempt})", method, path, status, attempt);
}

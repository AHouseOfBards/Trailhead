using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Trailhead.Health;

/// <summary>What a check from outside found (T9, A7). Only <see cref="Working"/> means proven.</summary>
internal enum OutsideState
{
    /// <summary>No check ran: it is switched off, or nothing is set up to check.</summary>
    NotChecked,

    /// <summary>Positive proof: our own one-time token came back from outside.</summary>
    Working,

    /// <summary>Something outside connected to the port. Not proof: an ISP filter or the router's own page can accept a connection too (A7).</summary>
    PortOpen,

    /// <summary>Every outside node failed to connect.</summary>
    NotReachable,

    /// <summary>The checker didn't give an answer that decides anything.</summary>
    CouldNotCheck,
}

/// <summary>An outside check's answer. <paramref name="Reason"/> is a code (<c>timeout</c>, <c>refused</c>…), never the checker's text.</summary>
internal sealed record OutsideResult(OutsideState State, string? Reason = null);

/// <summary>Asks something on the internet whether <c>host:port</c> answers.</summary>
internal interface IOutsideCheck
{
    /// <summary>Never throws except <see cref="OperationCanceledException"/> for <paramref name="ct"/>.</summary>
    Task<OutsideResult> CheckAsync(string host, int port, CancellationToken ct);
}

/// <summary>
/// The switch in front of the third-party checker (plan-finish choice 1, T17). Two things must both be on: the
/// owner's constant, flipped in code once check-host.net has confirmed its terms for automated use in writing, and
/// the admin's own setting. With either off, the answer is <see cref="OutsideState.NotChecked"/> and nothing leaves
/// the server: the hostname would otherwise go to a third party that publishes a report page for every check.
/// </summary>
internal sealed class OutsideCheckSwitch : IOutsideCheck
{
    /// <summary>
    /// False until check-host.net's operator has said in writing that this use is fine (reach-checkers.md "Unknowns").
    /// The owner flips it; nothing else may.
    /// </summary>
    internal const bool CheckHostTermsConfirmed = false;

    private readonly IOutsideCheck _inner;
    private readonly Func<bool> _allowed;
    private readonly bool _termsConfirmed;

    internal OutsideCheckSwitch(IOutsideCheck inner, Func<bool> allowedByAdmin, bool termsConfirmed = CheckHostTermsConfirmed)
    {
        _inner = inner;
        _allowed = allowedByAdmin;
        _termsConfirmed = termsConfirmed;
    }

    /// <summary>Both switches on.</summary>
    internal bool Enabled
    {
        get
        {
            try
            {
                return _termsConfirmed && _allowed();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public async Task<OutsideResult> CheckAsync(string host, int port, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new OutsideResult(OutsideState.NotChecked, "switched_off");
        }

        try
        {
            return await _inner.CheckAsync(host, port, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new OutsideResult(OutsideState.CouldNotCheck, "checker_failed");
        }
    }
}

/// <summary>
/// check-host.net's TCP check (https://check-host.net/about/api): <c>GET /check-tcp?host=name:port&amp;max_nodes=N</c>
/// with <c>Accept: application/json</c> answers a <c>request_id</c>; <c>GET /check-result/{id}</c> then answers, per
/// node, <c>null</c> while it is still checking, <c>[{"time":0.03,"address":"…"}]</c> when it connected, or
/// <c>[{"error":"Connection timed out"}]</c>. A TCP connect can't carry our token, so the best this can say is
/// <see cref="OutsideState.PortOpen"/>, never <see cref="OutsideState.Working"/> (A7).
/// </summary>
internal sealed class CheckHostClient : IOutsideCheck, IDisposable
{
    internal static readonly Uri DefaultBase = new("https://check-host.net/");

    private const int MaxBody = 64 * 1024;

    private readonly HttpClient _http;
    private readonly bool _owns;
    private readonly Uri _base;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollEvery;
    private readonly TimeSpan _pollFor;

    internal CheckHostClient(HttpClient? http, ILogger logger, TimeProvider? time = null, Uri? baseUrl = null, TimeSpan? pollEvery = null, TimeSpan? pollFor = null)
    {
        _owns = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(20) };
        _base = baseUrl ?? DefaultBase;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _pollEvery = pollEvery ?? TimeSpan.FromSeconds(2);
        _pollFor = pollFor ?? TimeSpan.FromSeconds(30);
    }

    public async Task<OutsideResult> CheckAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            var target = Uri.EscapeDataString(host + ":" + port.ToString(CultureInfo.InvariantCulture));
            string? id;
            using (var start = await GetAsync("check-tcp?host=" + target + "&max_nodes=3", ct).ConfigureAwait(false))
            {
                id = start is { RootElement.ValueKind: JsonValueKind.Object } && start.RootElement.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            }

            if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiLetterOrDigit))
            {
                return new OutsideResult(OutsideState.CouldNotCheck, "no_request");
            }

            var until = _time.GetUtcNow() + _pollFor;
            while (true)
            {
                await Task.Delay(_pollEvery, _time, ct).ConfigureAwait(false);
                using var result = await GetAsync("check-result/" + id, ct).ConfigureAwait(false);
                var verdict = Read(result);
                if (verdict is not null || _time.GetUtcNow() >= until)
                {
                    return verdict ?? new OutsideResult(OutsideState.CouldNotCheck, "no_answer");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogInformation("The outside check didn't answer ({Error})", e.GetType().Name);
            return new OutsideResult(OutsideState.CouldNotCheck, "checker_failed");
        }
    }

    /// <summary>The verdict once every node has answered, or one connected; null while any is still checking.</summary>
    internal static OutsideResult? Read(JsonDocument? doc)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var nodes = 0;
        var pending = 0;
        var timeouts = 0;
        var refused = 0;
        foreach (var node in doc.RootElement.EnumerateObject())
        {
            nodes++;
            var v = node.Value;
            if (v.ValueKind == JsonValueKind.Null)
            {
                pending++;
                continue;
            }

            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0 || v[0].ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entry = v[0];
            if (entry.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.Number && entry.TryGetProperty("address", out _))
            {
                return new OutsideResult(OutsideState.PortOpen, "connected");
            }

            if (entry.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            {
                var text = err.GetString() ?? string.Empty;
                if (text.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    refused++;
                }
                else
                {
                    timeouts++;
                }
            }
        }

        if (nodes == 0 || pending > 0)
        {
            return null;
        }

        if (refused + timeouts == 0)
        {
            return new OutsideResult(OutsideState.CouldNotCheck, "no_answer");
        }

        return new OutsideResult(OutsideState.NotReachable, refused > 0 ? "refused" : "timeout");
    }

    public void Dispose()
    {
        if (_owns)
        {
            _http.Dispose();
        }
    }

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBody)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Jellyfin.Plugin.Trailhead.Edge;

/// <summary>
/// The header names each HTTP/1.1 request on a connection nominated in its Connection header, read off the wire.
/// Kestrel keeps only the options it understands (keep-alive, close, upgrade) and drops the rest of the value before
/// the app sees it: a raw-socket request with <c>Connection: keep-alive, X-Hop</c> reaches the app as
/// <c>keep-alive</c> (spike T-S1 2b, and EdgeProxyTests over a raw socket). RFC 9110 §7.6.1 says those named headers
/// must go, so this reads the decrypted request bytes once as they pass, keeps each request's names in order, and
/// the pipeline takes one entry per request.
/// </summary>
/// <remarks>
/// Kestrel works through a connection's requests one at a time and in order, and closes the connection after a request
/// it rejects, so the n-th head read here is the n-th request the pipeline sees. The bodies are skipped by their framing
/// (Content-Length or chunked). When the stream can't be followed (an upgrade, or bytes that don't parse) the rest is
/// passed through unread and <see cref="Lost"/> is set; requests after that get no entry and the pipeline refuses them
/// rather than forward headers it couldn't check.
/// </remarks>
internal sealed class ConnectionNames
{
    internal const string ItemKey = "trailhead.connection-names";

    // Kestrel's own limit for a request head is 32 KB; anything past this is refused by Kestrel anyway.
    private const int MaxHead = 64 * 1024;

    private readonly ConcurrentQueue<string[]> _heads = new();
    private readonly List<byte> _head = new();
    private State _state = State.Head;
    private long _remaining;
    private readonly List<byte> _line = new();

    private enum State
    {
        Head,
        Body,
        ChunkSize,
        ChunkData,
        ChunkDataEnd,
        Trailers,
        PassThrough,
    }

    /// <summary>True once the stream couldn't be followed: later requests have no entry.</summary>
    internal bool Lost { get; private set; }

    /// <summary>The upgrade case: after it the bytes aren't HTTP any more, and nothing is lost by not reading them.</summary>
    internal bool Upgraded { get; private set; }

    /// <summary>The names the next request nominated, or null when there is no entry for it.</summary>
    internal string[]? Next() => _heads.TryDequeue(out var names) ? names : null;

    /// <summary>
    /// A connection middleware, after TLS: HTTP/1.x connections get their request bytes copied through a scanner on the
    /// way to Kestrel. HTTP/2 has no Connection header (RFC 9113 §8.2.2) and is passed through untouched.
    /// </summary>
    internal static Func<ConnectionDelegate, ConnectionDelegate> Middleware => next => async connection =>
    {
        var alpn = connection.Features.Get<ITlsApplicationProtocolFeature>()?.ApplicationProtocol;
        if (alpn is { Length: > 0 } p && p.Span.SequenceEqual("h2"u8))
        {
            await next(connection).ConfigureAwait(false);
            return;
        }

        var names = new ConnectionNames();
        connection.Items[ItemKey] = names;
        var original = connection.Transport;
        var pipe = new Pipe();
        connection.Transport = new Duplex(pipe.Reader, original.Output);
        var pump = names.PumpAsync(original.Input, pipe.Writer);
        try
        {
            await next(connection).ConfigureAwait(false);
        }
        finally
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            connection.Transport = original;
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection is gone either way.
            }
        }
    };

    /// <summary>The entry for this request, from the connection it came on. Null when there is none.</summary>
    internal static string[]? ForRequest(HttpContext ctx, out bool scanned)
    {
        scanned = false;
        if (ctx.Request.Protocol != "HTTP/1.1" && ctx.Request.Protocol != "HTTP/1.0")
        {
            // HTTP/2 can't carry Connection; nothing to read.
            return Array.Empty<string>();
        }

        if (ctx.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ItemKey, out var value) != true || value is not ConnectionNames names)
        {
            return null;
        }

        scanned = true;
        return names.Next();
    }

    /// <summary>Feeds bytes in arrival order. Public for the scanner's own tests.</summary>
    internal void Feed(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && _state != State.PassThrough)
        {
            switch (_state)
            {
                case State.Head:
                    _head.Add(data[i++]);
                    if (EndsHead())
                    {
                        ParseHead();
                    }
                    else if (_head.Count > MaxHead)
                    {
                        Lose();
                    }

                    break;

                case State.Body:
                case State.ChunkData:
                {
                    var take = (int)Math.Min(_remaining, data.Length - i);
                    i += take;
                    _remaining -= take;
                    if (_remaining == 0)
                    {
                        _state = _state == State.Body ? State.Head : State.ChunkDataEnd;
                    }

                    break;
                }

                case State.ChunkSize:
                case State.ChunkDataEnd:
                case State.Trailers:
                    if (TakeLine(data[i++]) is { } line)
                    {
                        OnLine(line);
                    }

                    break;
            }
        }
    }

    private async Task PumpAsync(PipeReader input, PipeWriter output)
    {
        try
        {
            while (true)
            {
                var read = await input.ReadAsync().ConfigureAwait(false);
                foreach (var segment in read.Buffer)
                {
                    Feed(segment.Span);
                    var flushed = await output.WriteAsync(segment).ConfigureAwait(false);
                    if (flushed.IsCompleted)
                    {
                        input.AdvanceTo(read.Buffer.End);
                        return;
                    }
                }

                input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted || read.IsCanceled)
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            await output.CompleteAsync(e).ConfigureAwait(false);
            return;
        }
        finally
        {
            await input.CompleteAsync().ConfigureAwait(false);
        }
    }

    private bool EndsHead()
    {
        var n = _head.Count;
        if (n == 2 && _head[0] == '\r' && _head[1] == '\n')
        {
            // A blank line between requests (RFC 9112 §2.2 lets a server skip it).
            _head.Clear();
            return false;
        }

        return n >= 4 && _head[n - 1] == '\n' && _head[n - 2] == '\r' && _head[n - 3] == '\n' && _head[n - 4] == '\r';
    }

    private void ParseHead()
    {
        var text = Encoding.Latin1.GetString(_head.ToArray());
        _head.Clear();
        var lines = text.Split("\r\n");
        var names = new List<string>();
        long? length = null;
        var chunked = false;
        var upgrade = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                names.AddRange(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var l) || (length is { } before && before != l))
                {
                    Lose();
                    return;
                }

                length = l;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                chunked |= value.Split(',', StringSplitOptions.TrimEntries).Contains("chunked", StringComparer.OrdinalIgnoreCase);
            }
            else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                upgrade = true;
            }
        }

        _heads.Enqueue(names.ToArray());
        if (upgrade && names.Contains("upgrade", StringComparer.OrdinalIgnoreCase))
        {
            // Past a 101 the bytes are WebSocket frames. Nothing after this is a request Edge forwards as HTTP.
            Upgraded = true;
            _state = State.PassThrough;
        }
        else if (chunked)
        {
            _state = State.ChunkSize;
        }
        else if (length is > 0)
        {
            _remaining = length.Value;
            _state = State.Body;
        }
    }

    private void OnLine(string line)
    {
        switch (_state)
        {
            case State.ChunkSize:
            {
                var size = line.Split(';')[0].Trim();
                if (!long.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) || n < 0)
                {
                    Lose();
                    return;
                }

                if (n == 0)
                {
                    _state = State.Trailers;
                }
                else
                {
                    _remaining = n;
                    _state = State.ChunkData;
                }

                break;
            }

            case State.ChunkDataEnd:
                if (line.Length != 0)
                {
                    Lose();
                    return;
                }

                _state = State.ChunkSize;
                break;

            case State.Trailers:
                if (line.Length == 0)
                {
                    _state = State.Head;
                }

                break;
        }
    }

    // A CRLF-terminated line, or null while it is still coming.
    private string? TakeLine(byte b)
    {
        _line.Add(b);
        var n = _line.Count;
        if (n >= 2 && _line[n - 1] == '\n' && _line[n - 2] == '\r')
        {
            var line = Encoding.Latin1.GetString(_line.ToArray(), 0, n - 2);
            _line.Clear();
            return line;
        }

        if (n > MaxHead)
        {
            Lose();
        }

        return null;
    }

    private void Lose()
    {
        Lost = true;
        _state = State.PassThrough;
    }

    private sealed class Duplex : IDuplexPipe
    {
        internal Duplex(PipeReader input, PipeWriter output)
        {
            Input = input;
            Output = output;
        }

        public PipeReader Input { get; }

        public PipeWriter Output { get; }
    }
}

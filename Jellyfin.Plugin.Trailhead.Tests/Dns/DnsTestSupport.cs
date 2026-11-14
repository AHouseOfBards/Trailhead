using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Trailhead.Dns;
using Microsoft.Extensions.Time.Testing;

namespace Jellyfin.Plugin.Trailhead.Tests.Dns;

internal static class Clock
{
    /// <summary>
    /// Runs the clock while a task waits on it: a retry's backoff and the waiter's poll interval are TimeProvider delays,
    /// so they only end when the fake time moves. The real pause between steps lets the task run in between.
    /// </summary>
    internal static async Task<T> PumpAsync<T>(Task<T> task, FakeTimeProvider time, TimeSpan? step = null)
    {
        await PumpAsync((Task)task, time, step);
        return task.Result;
    }

    internal static async Task PumpAsync(Task task, FakeTimeProvider time, TimeSpan? step = null)
    {
        var giveUp = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException("The task did not finish while the fake clock ran.");
            }

            time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        await task;
    }

    /// <summary>For a wait that should be over at once. If the code keeps waiting, the test fails instead of hanging.</summary>
    internal static async Task WithinAsync(Task task, int seconds = 5)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"The wait did not finish within {seconds} s; it should have been over at once.");
        }
    }

    internal static async Task UntilAsync(Func<bool> condition, string what)
    {
        var giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException("Gave up waiting for " + what + ".");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>The waiter's DNS, answered from tables, with every call noted.</summary>
internal sealed class FakeLookups : IDnsLookups
{
    private readonly object _gate = new();
    private readonly List<string> _nsAsked = new();
    private readonly List<(IPEndPoint Server, string Name)> _txtAsked = new();

    internal Dictionary<string, string[]> Ns { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, IPAddress[]> V4 { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, IPAddress[]> V6 { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> NsThrows { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The TXT values each server serves. A server missing from it can't be asked.</summary>
    internal Dictionary<IPEndPoint, string[]> Txt { get; } = new();

    internal IReadOnlyList<string> NsAsked
    {
        get
        {
            lock (_gate)
            {
                return _nsAsked.ToList();
            }
        }
    }

    internal IReadOnlyList<(IPEndPoint Server, string Name)> TxtAsked
    {
        get
        {
            lock (_gate)
            {
                return _txtAsked.ToList();
            }
        }
    }

    public Task<IReadOnlyList<string>> LookupNsAsync(string zone, CancellationToken ct)
    {
        lock (_gate)
        {
            _nsAsked.Add(zone);
        }

        if (NsThrows.Contains(zone))
        {
            throw new InvalidOperationException("the resolver is down");
        }

        return Task.FromResult<IReadOnlyList<string>>(Ns.TryGetValue(zone, out var ns) ? ns : Array.Empty<string>());
    }

    public Task<IReadOnlyList<IPAddress>> LookupAddressesAsync(string host, AddressFamily family, CancellationToken ct)
    {
        var table = family == AddressFamily.InterNetworkV6 ? V6 : V4;
        return Task.FromResult<IReadOnlyList<IPAddress>>(table.TryGetValue(host, out var a) ? a : Array.Empty<IPAddress>());
    }

    public Task<IReadOnlyList<string>> QueryTxtAsync(IPEndPoint server, string name, CancellationToken ct)
    {
        lock (_gate)
        {
            _txtAsked.Add((server, name));
            if (!Txt.TryGetValue(server, out var values))
            {
                throw new TimeoutException("the server did not answer");
            }

            return Task.FromResult<IReadOnlyList<string>>(values);
        }
    }
}

/// <summary>
/// A DNS server on a real UDP socket (127.0.0.1, any free port), for the DnsClient code. It answers NS, A and TXT
/// from tables and notes whether each query had the recursion-desired bit set, which the waiter must leave off.
/// </summary>
internal sealed class FakeDnsServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private readonly List<(string Name, int Type, bool RecursionDesired)> _queries = new();

    internal FakeDnsServer()
    {
        _loop = Task.Run(LoopAsync);
    }

    internal IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    internal Dictionary<string, string[]> Ns { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, IPAddress> A { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, string[]> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<(string Name, int Type, bool RecursionDesired)> Queries
    {
        get
        {
            lock (_gate)
            {
                return _queries.ToList();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
    }

    private static void Name(List<byte> to, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            to.Add((byte)label.Length);
            to.AddRange(Encoding.ASCII.GetBytes(label));
        }

        to.Add(0);
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp.ReceiveAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var q = received.Buffer;
            var pos = 12;
            var labels = new List<string>();
            while (q[pos] != 0)
            {
                labels.Add(Encoding.ASCII.GetString(q, pos + 1, q[pos]));
                pos += q[pos] + 1;
            }

            pos++;
            var type = (q[pos] << 8) | q[pos + 1];
            var name = string.Join('.', labels);
            var rd = (q[2] & 0x01) != 0;
            lock (_gate)
            {
                _queries.Add((name, type, rd));
            }

            var answers = new List<byte[]>();
            switch (type)
            {
                case 2 when Ns.TryGetValue(name, out var ns):
                    foreach (var host in ns)
                    {
                        var data = new List<byte>();
                        Name(data, host);
                        answers.Add(data.ToArray());
                    }

                    break;
                case 1 when A.TryGetValue(name, out var ip):
                    answers.Add(ip.GetAddressBytes());
                    break;
                case 16 when Txt.TryGetValue(name, out var txt):
                    foreach (var value in txt)
                    {
                        var bytes = Encoding.ASCII.GetBytes(value);
                        answers.Add(new[] { (byte)bytes.Length }.Concat(bytes).ToArray());
                    }

                    break;
            }

            var r = new List<byte> { q[0], q[1], (byte)(0x84 | (q[2] & 0x01)), 0, 0, 1, (byte)(answers.Count >> 8), (byte)answers.Count, 0, 0, 0, 0 };
            r.AddRange(q.Skip(12).Take(pos + 4 - 12));
            foreach (var data in answers)
            {
                // A pointer back to the question's name, then type, class IN, TTL 1, and the data.
                r.AddRange(new byte[] { 0xC0, 0x0C, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 1, (byte)(data.Length >> 8), (byte)data.Length });
                r.AddRange(data);
            }

            try
            {
                await _udp.SendAsync(r.ToArray(), received.RemoteEndPoint);
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }
}

using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Trailhead.Tests.Acme;

/// <summary>A tiny HTTP server on 127.0.0.1 that serves one DER file and counts the connections it gets.</summary>
internal sealed class CaIssuersServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private byte[] _body = Array.Empty<byte>();
    private Task _loop = Task.CompletedTask;
    private int _requests;

    internal CaIssuersServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/root.cer";
    }

    internal string Url { get; }

    internal int Requests => Volatile.Read(ref _requests);

    internal void Serve(byte[] der)
    {
        _body = der;
        _loop = Task.Run(Accept);
    }

    internal async Task StopAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Interlocked.Increment(ref _requests);
            var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer, _stop.Token);
            var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/pkix-cert\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, _stop.Token);
            await stream.WriteAsync(_body, _stop.Token);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetworkOptimizer.Web.Tests.Services.RoutedDevices;

/// <summary>
/// A plain-HTTP server on a loopback port that answers each request from a path-to-response map
/// and counts the connections it accepts. Stands in for a device, or for a service on this server's
/// loopback; a TLS handshake is closed at once so an HTTPS fallback fails fast.
/// </summary>
internal sealed class FakeDeviceHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _accepted;
    private readonly ConcurrentQueue<string> _hostHeaders = new();

    public FakeDeviceHttpServer(Func<string, string> respond)
    {
        _respond = respond;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Connections accepted so far.</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>The Host header of each request served, in order.</summary>
    public IReadOnlyCollection<string> HostHeaders => _hostHeaders;

    public static string Redirect(string location) =>
        $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    public static string Ok(string body, string contentType = "text/plain")
    {
        var length = Encoding.UTF8.GetByteCount(body);
        return $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n{body}";
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _accepted);
                _ = ServeAsync(client);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0 || (request.Length == 0 && buffer[0] == 0x16))
                        return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var head = request.ToString();
                var path = head.Split(' ')[1];
                var host = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
                _hostHeaders.Enqueue(host?[5..].Trim() ?? "");
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_respond(path)), _cts.Token);
            }
        }
        catch (Exception)
        {
            // A client that gives up mid-request is not the test's concern.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _acceptLoop;
        _cts.Dispose();
    }
}

using System.Collections.Concurrent;
using System.Net.Sockets;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The site side of tunnel TCP proxying: the server asks for a connection to
/// a site-local host:port (SSH, the UniFi Console), this handler dials it and
/// pumps bytes both ways under the server-assigned connection id. Lives and
/// dies with its tunnel connection, like the probe runner.
/// </summary>
public sealed class ProxyHandler
{
    private const int FrameBytes = 32 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly TunnelClient _tunnel;
    private readonly ConcurrentDictionary<long, TcpClient> _connections = new();

    public ProxyHandler(TunnelClient tunnel)
    {
        _tunnel = tunnel;
    }

    public async Task HandleOpenAsync(ProxyOpen open, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(open.Host, open.Port, connectCts.Token);
        }
        catch (Exception ex)
        {
            client.Dispose();
            await _tunnel.SendAsync(new AgentMessage
            {
                ProxyOpenResult = new ProxyOpenResult
                {
                    ConnectionId = open.ConnectionId,
                    Ok = false,
                    Error = ex is OperationCanceledException ? "connect timed out" : ex.Message,
                }
            }, ct);
            return;
        }

        _connections[open.ConnectionId] = client;
        await _tunnel.SendAsync(new AgentMessage
        {
            ProxyOpenResult = new ProxyOpenResult { ConnectionId = open.ConnectionId, Ok = true }
        }, ct);

        // Site-to-server pump; the server-to-site direction arrives via
        // HandleDataAsync from the tunnel read loop.
        var buffer = new byte[FrameBytes];
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, ct); }
                catch { break; }
                if (read <= 0) break;
                var ok = await _tunnel.SendAsync(new AgentMessage
                {
                    ProxyData = new ProxyData { ConnectionId = open.ConnectionId, Data = ByteString.CopyFrom(buffer, 0, read) }
                }, ct);
                if (!ok) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_connections.TryRemove(open.ConnectionId, out var removed))
            {
                _tunnel.TrySend(new AgentMessage
                {
                    ProxyClose = new ProxyClose { ConnectionId = open.ConnectionId }
                });
                try { removed.Dispose(); } catch { }
            }
        }
    }

    public async Task HandleDataAsync(ProxyData data, CancellationToken ct)
    {
        if (!_connections.TryGetValue(data.ConnectionId, out var client)) return;
        try
        {
            await client.GetStream().WriteAsync(data.Data.Memory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            HandleClose(new ProxyClose { ConnectionId = data.ConnectionId });
        }
    }

    public void HandleClose(ProxyClose close)
    {
        if (_connections.TryRemove(close.ConnectionId, out var client))
        {
            try { client.Dispose(); } catch { }
        }
    }
}

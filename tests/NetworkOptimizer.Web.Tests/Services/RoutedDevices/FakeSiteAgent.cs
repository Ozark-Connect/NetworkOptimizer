using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Tests.Services.RoutedDevices;

/// <summary>
/// An agent on a site's LAN, connected to <see cref="AgentTunnelProxyService"/> the way the real tunnel
/// handler connects one. Opens are answered from <c>lan</c>, a map from the address the server asks
/// for to the local test server that plays it; an address not on the map is unreachable.
/// </summary>
internal sealed class FakeSiteAgent : IAsyncDisposable
{
    private readonly AgentTunnelProxyService _proxy;
    private readonly AgentTunnelConnection _connection;
    private readonly IReadOnlyDictionary<(string Host, int Port), int> _lan;
    private readonly ConcurrentDictionary<long, TcpClient> _sockets = new();
    private readonly ConcurrentQueue<(string Host, int Port)> _opens = new();
    private int _closesFromServer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeSiteAgent(
        AgentTunnelRegistry registry, AgentTunnelProxyService proxy, string siteSlug,
        IReadOnlyDictionary<(string Host, int Port), int> lan)
    {
        _proxy = proxy;
        _lan = lan;
        _connection = registry.Register(agentId: 1, siteSlug, "Test Agent");
        _loop = RunAsync();
    }

    /// <summary>Every address the server asked this agent to open, in order.</summary>
    public IReadOnlyCollection<(string Host, int Port)> Opens => _opens;

    /// <summary>How many connections the server has closed from its side.</summary>
    public int ClosesFromServer => Volatile.Read(ref _closesFromServer);

    private async Task RunAsync()
    {
        try
        {
            await foreach (var message in _connection.Outbound.ReadAllAsync(_cts.Token))
            {
                switch (message.PayloadCase)
                {
                    case ServerMessage.PayloadOneofCase.ProxyOpen:
                        await OpenAsync(message.ProxyOpen);
                        break;
                    case ServerMessage.PayloadOneofCase.ProxyData:
                        if (_sockets.TryGetValue(message.ProxyData.ConnectionId, out var target))
                            await target.GetStream().WriteAsync(message.ProxyData.Data.Memory, _cts.Token);
                        break;
                    case ServerMessage.PayloadOneofCase.ProxyClose:
                        Interlocked.Increment(ref _closesFromServer);
                        if (_sockets.TryRemove(message.ProxyClose.ConnectionId, out var closed))
                            closed.Dispose();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OpenAsync(ProxyOpen open)
    {
        _opens.Enqueue((open.Host, open.Port));
        if (!_lan.TryGetValue((open.Host, open.Port), out var localPort))
        {
            _proxy.OnProxyOpenResult(_connection, new ProxyOpenResult { ConnectionId = open.ConnectionId, Ok = false, Error = "no route to host" });
            return;
        }

        var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, localPort, _cts.Token);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            _proxy.OnProxyOpenResult(_connection, new ProxyOpenResult { ConnectionId = open.ConnectionId, Ok = false, Error = ex.Message });
            return;
        }
        _sockets[open.ConnectionId] = socket;
        _proxy.OnProxyOpenResult(_connection, new ProxyOpenResult { ConnectionId = open.ConnectionId, Ok = true });
        _ = RelayToServerAsync(open.ConnectionId, socket);
    }

    private async Task RelayToServerAsync(long connectionId, TcpClient socket)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await socket.GetStream().ReadAsync(buffer, _cts.Token)) > 0)
            {
                await _proxy.OnProxyDataAsync(_connection,
                    new ProxyData { ConnectionId = connectionId, Data = ByteString.CopyFrom(buffer, 0, read) }, _cts.Token);
            }
        }
        catch (Exception)
        {
            // The server or the test tore the connection down; the close below still reports it.
        }
        _proxy.OnProxyClose(_connection, new ProxyClose { ConnectionId = connectionId });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop;
        foreach (var socket in _sockets.Values)
            socket.Dispose();
        _cts.Dispose();
    }
}

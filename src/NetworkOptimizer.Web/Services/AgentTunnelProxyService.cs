using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Reaches TCP services inside an agent's site through the tunnel (SSH, the
/// UniFi Console API). For each requested site-local host:port this service
/// binds a loopback listener on the central server; every connection accepted
/// there is multiplexed over the site's agent tunnel, where the agent dials
/// the real host:port and pumps bytes both ways. Existing code paths (HTTP
/// clients, SSH.NET) then just talk to 127.0.0.1:{proxyPort} - the proxying
/// is invisible to them.
/// </summary>
public class AgentTunnelProxyService : IDisposable
{
    private const int FrameBytes = 32 * 1024;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

    private readonly AgentTunnelRegistry _registry;
    private readonly ILogger<AgentTunnelProxyService> _logger;

    private readonly ConcurrentDictionary<string, ProxyListener> _listeners = new();
    private readonly ConcurrentDictionary<long, ProxyConnection> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextConnectionId;

    public AgentTunnelProxyService(AgentTunnelRegistry registry, ILogger<AgentTunnelProxyService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Loopback port that proxies to {host}:{port} inside the given site.
    /// Idempotent per (site, host, port); the listener lives for the app's
    /// lifetime and resolves the site's live agent per accepted connection,
    /// so agent reconnects don't invalidate the endpoint.
    /// </summary>
    public int GetOrCreateEndpoint(string siteSlug, string host, int port)
    {
        var listener = _listeners.GetOrAdd($"{siteSlug}|{host}:{port}", key =>
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var created = new ProxyListener(tcp, siteSlug, host, port);
            _ = AcceptLoopAsync(created, _shutdown.Token);
            _logger.LogInformation("Agent proxy listening on 127.0.0.1:{Local} -> {Host}:{Port} (site {Slug})",
                created.LocalPort, host, port, siteSlug);
            return created;
        });
        return listener.LocalPort;
    }

    private async Task AcceptLoopAsync(ProxyListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.Tcp.AcceptTcpClientAsync(ct);
                _ = HandleLocalConnectionAsync(listener, client, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent proxy accept loop for {Host}:{Port} (site {Slug}) stopped",
                listener.TargetHost, listener.TargetPort, listener.SiteSlug);
        }
    }

    private async Task HandleLocalConnectionAsync(ProxyListener listener, TcpClient client, CancellationToken ct)
    {
        var agent = _registry.GetForSite(listener.SiteSlug).FirstOrDefault();
        if (agent == null)
        {
            _logger.LogDebug("Proxy connect to {Host}:{Port} refused - no agent online for site {Slug}",
                listener.TargetHost, listener.TargetPort, listener.SiteSlug);
            client.Dispose();
            return;
        }

        var id = Interlocked.Increment(ref _nextConnectionId);
        var connection = new ProxyConnection(id, client, agent);
        _connections[id] = connection;
        try
        {
            var sent = await agent.SendAsync(new ServerMessage
            {
                ProxyOpen = new ProxyOpen { ConnectionId = id, Host = listener.TargetHost, Port = listener.TargetPort }
            }, ct);
            if (!sent)
            {
                CloseConnection(connection, notifyAgent: false);
                return;
            }

            using var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            openTimeout.CancelAfter(OpenTimeout);
            string? openError;
            try
            {
                openError = await connection.OpenResult.Task.WaitAsync(openTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                openError = "open timed out";
            }
            if (openError != null)
            {
                _logger.LogDebug("Proxy open {Host}:{Port} via agent {AgentId} failed: {Error}",
                    listener.TargetHost, listener.TargetPort, agent.AgentId, openError);
                CloseConnection(connection, notifyAgent: false);
                return;
            }

            // Local reads -> tunnel, with backpressure. The agent-to-local
            // direction is written by the tunnel read loop via OnProxyDataAsync.
            var buffer = new byte[FrameBytes];
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, ct); }
                catch { break; }
                if (read <= 0) break;
                var ok = await agent.SendAsync(new ServerMessage
                {
                    ProxyData = new ProxyData { ConnectionId = id, Data = ByteString.CopyFrom(buffer, 0, read) }
                }, ct);
                if (!ok) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CloseConnection(connection, notifyAgent: true);
        }
    }

    /// <summary>Agent answered a ProxyOpen.</summary>
    public void OnProxyOpenResult(ProxyOpenResult result)
    {
        if (_connections.TryGetValue(result.ConnectionId, out var connection))
            connection.OpenResult.TrySetResult(result.Ok ? null : (string.IsNullOrEmpty(result.Error) ? "connect failed" : result.Error));
    }

    /// <summary>
    /// Agent-to-local bytes. Called sequentially from the tunnel read loop, so
    /// writes stay ordered; a wedged local reader can stall that loop, which
    /// is the accepted trade-off of the single-stream design.
    /// </summary>
    public async Task OnProxyDataAsync(ProxyData data, CancellationToken ct)
    {
        if (!_connections.TryGetValue(data.ConnectionId, out var connection)) return;
        try
        {
            await connection.Client.GetStream().WriteAsync(data.Data.Memory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseConnection(connection, notifyAgent: true);
        }
    }

    /// <summary>Agent reports the site-side socket closed.</summary>
    public void OnProxyClose(ProxyClose close)
    {
        if (_connections.TryGetValue(close.ConnectionId, out var connection))
            CloseConnection(connection, notifyAgent: false);
    }

    /// <summary>Tears down every proxied connection riding a dropped tunnel.</summary>
    public void OnAgentDisconnected(AgentTunnelConnection agent)
    {
        foreach (var connection in _connections.Values.Where(c => ReferenceEquals(c.Agent, agent)))
            CloseConnection(connection, notifyAgent: false);
    }

    private void CloseConnection(ProxyConnection connection, bool notifyAgent)
    {
        if (!_connections.TryRemove(connection.Id, out _)) return;
        connection.OpenResult.TrySetResult("closed");
        if (notifyAgent)
            connection.Agent.TrySend(new ServerMessage { ProxyClose = new ProxyClose { ConnectionId = connection.Id } });
        try { connection.Client.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var listener in _listeners.Values)
        {
            try { listener.Tcp.Stop(); } catch { }
        }
        foreach (var connection in _connections.Values)
            CloseConnection(connection, notifyAgent: false);
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record ProxyListener(TcpListener Tcp, string SiteSlug, string TargetHost, int TargetPort)
    {
        public int LocalPort => ((IPEndPoint)Tcp.LocalEndpoint).Port;
    }

    private sealed class ProxyConnection
    {
        public ProxyConnection(long id, TcpClient client, AgentTunnelConnection agent)
        {
            Id = id;
            Client = client;
            Agent = agent;
        }

        public long Id { get; }
        public TcpClient Client { get; }
        public AgentTunnelConnection Agent { get; }

        /// <summary>Null = opened; otherwise the failure reason.</summary>
        public TaskCompletionSource<string?> OpenResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

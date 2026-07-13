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

    // A live agent answers a ProxyOpen in well under a second (it's a local dial
    // on its side), so a tight timeout still never trips a healthy tunnel but
    // bounds the per-connection stall when the tunnel is black-holed and the
    // answer never comes.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(3);

    // Past AgentTunnelConnection.StaleThreshold of silence the tunnel is treated
    // as black-holed and proxy opens are refused immediately instead of blocking
    // - well before the 90s server watchdog drops the dead tunnel.

    // After an open to a site times out, fast-fail further opens instead of each
    // eating the full OpenTimeout. A page render (a site switch) fires a burst of
    // console + SSH opens; in the first ~45s of an outage - before the liveness
    // gate above can trip without false-failing a healthy tunnel - that burst
    // would otherwise block OpenTimeout on every one, a 15s+ hang on the switch.
    // Hold past the 45s gate so the breaker never expires and re-probes
    // mid-outage; a shorter hold let a fresh burst time out every ~15s, a ~10s
    // stall on any switch landing in that window. It still clears the instant the
    // tunnel produces fresh inbound (recovery) - see the LastMessageAt check
    // below - so a healthy tunnel is never held this long: its next heartbeat
    // (<=30s) advances LastMessageAt and lets the open through.
    private static readonly TimeSpan OpenBreakerHold = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (DateTime Until, DateTime OpenedAtLastMsg)> _openBreaker = new();

    private readonly AgentTunnelRegistry _registry;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly ILogger<AgentTunnelProxyService> _logger;

    private readonly ConcurrentDictionary<string, ProxyListener> _listeners = new();
    private readonly ConcurrentDictionary<long, ProxyConnection> _connections = new();
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextConnectionId;

    public AgentTunnelProxyService(AgentTunnelRegistry registry, SiteConnectionRegistry siteConnections, ILogger<AgentTunnelProxyService> logger)
    {
        _registry = registry;
        _siteConnections = siteConnections;
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

        // A black-holed tunnel stays registered (IsAgentOnline() true) until the
        // 90s server watchdog drops it. Until then this dial would write into the
        // dead socket and block the full OpenTimeout waiting for an answer that
        // never comes - a multi-second freeze on every page load and site switch.
        // If the tunnel has gone silent past the heartbeat window, treat it as
        // down and refuse immediately so the console falls through to its
        // error/awaiting-agent state instead of hanging the browser.
        var silent = DateTime.UtcNow - agent.LastMessageAt;
        if (silent > AgentTunnelConnection.StaleThreshold)
        {
            _logger.LogDebug("Proxy connect to {Host}:{Port} refused - agent {AgentId} silent for {Silent:n0}s (site {Slug})",
                listener.TargetHost, listener.TargetPort, agent.AgentId, silent.TotalSeconds, listener.SiteSlug);
            client.Dispose();
            // Belt-and-braces with the watchdog's proactive flip: if a dial reaches
            // a stale tunnel before the watchdog's next 15s tick has flipped the
            // console, flip it now so this page's remaining calls short-circuit.
            FlipConsoleAwaitingAgent(listener.SiteSlug);
            return;
        }

        // Circuit breaker: a recent open to this site timed out and no inbound has
        // arrived since, so the tunnel is almost certainly still black-holed.
        // Fast-fail the render's remaining opens rather than eat OpenTimeout on
        // each. Fresh inbound (LastMessageAt past when the breaker tripped) means
        // the tunnel recovered, clearing this without needing a probe.
        if (_openBreaker.TryGetValue(listener.SiteSlug, out var breaker)
            && DateTime.UtcNow < breaker.Until
            && agent.LastMessageAt <= breaker.OpenedAtLastMsg)
        {
            _logger.LogDebug("Proxy connect to {Host}:{Port} fast-refused - open breaker tripped for agent {AgentId} (site {Slug})",
                listener.TargetHost, listener.TargetPort, agent.AgentId, listener.SiteSlug);
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
                // Trip the breaker so the opens queued behind this one fast-fail
                // instead of each blocking the full OpenTimeout.
                var wasTripped = _openBreaker.TryGetValue(listener.SiteSlug, out var prev)
                                 && DateTime.UtcNow < prev.Until;
                _openBreaker[listener.SiteSlug] = (DateTime.UtcNow + OpenBreakerHold, agent.LastMessageAt);

                // On the FIRST timeout of an outage, flip the site's console to
                // awaiting-agent now (not at the 90s watchdog), so its page renders
                // short-circuit console calls instead of each dialing the dead proxy
                // and paying the retry backoff.
                if (!wasTripped)
                    FlipConsoleAwaitingAgent(listener.SiteSlug);
            }
            if (openError != null)
            {
                _logger.LogDebug("Proxy open {Host}:{Port} via agent {AgentId} failed: {Error}",
                    listener.TargetHost, listener.TargetPort, agent.AgentId, openError);
                CloseConnection(connection, notifyAgent: false);
                return;
            }

            // The tunnel answered, so clear any open breaker for this site.
            _openBreaker.TryRemove(listener.SiteSlug, out _);

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

    /// <summary>
    /// Whether this site's tunnel path is currently suspect: no agent, an agent
    /// silent past the stale threshold, or the open breaker tripped with no
    /// fresh inbound since. Lets the console's connect-failure handling tell "the
    /// tunnel is dead" (report awaiting-agent) apart from a genuine console-side
    /// failure reached over a healthy tunnel (report the real error).
    /// </summary>
    public bool IsTunnelSuspect(string siteSlug)
    {
        var agent = _registry.GetForSite(siteSlug).FirstOrDefault();
        if (agent == null || agent.IsStale) return true;
        return _openBreaker.TryGetValue(siteSlug, out var breaker)
               && DateTime.UtcNow < breaker.Until
               && agent.LastMessageAt <= breaker.OpenedAtLastMsg;
    }

    /// <summary>
    /// Flips the site's console to awaiting-agent off the dial path (fire-and-
    /// forget, idempotent) the moment a dead tunnel is proven - by an open
    /// timeout or a stale-gate refusal - so page renders short-circuit console
    /// calls instead of paying dial-and-retry per call until the 90s watchdog.
    /// </summary>
    private void FlipConsoleAwaitingAgent(string siteSlug)
    {
        _ = Task.Run(async () =>
        {
            try { await _siteConnections.GetFor(siteSlug).NoteTunnelUnreachableAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Awaiting-agent flip failed for site {Slug}", siteSlug); }
        });
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

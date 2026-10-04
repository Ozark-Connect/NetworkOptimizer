using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Reaches TCP services inside an agent's site through the tunnel (SSH, the
/// UniFi Console API, device status pages). Every connection is multiplexed over
/// the site's agent tunnel, where the agent dials the real host:port and pumps
/// bytes both ways. Two ways in: a loopback listener per site-local host:port,
/// for code that dials an address itself (SSH.NET, the console client), and
/// <see cref="OpenStreamAsync"/>, a stream for any address, for device clients, which
/// dial whatever address a device names, redirects included.
/// </summary>
public class AgentTunnelProxyService : IDisposable
{
    private const int FrameBytes = 32 * 1024;

    // A live agent answers a ProxyOpen in well under a second (it's a local dial
    // on its side), so a tight timeout still never trips a healthy tunnel but
    // bounds the per-connection stall when the tunnel is black-holed and the
    // answer never comes.
    // MUST stay longer than the agent's own ProxyHandler.ConnectTimeout (5s), or the agent's
    // answer for an unreachable target always arrives too late to be read and every dead host
    // looks identical to a dead tunnel. At 3s against the agent's 5s that was not a race, it
    // was guaranteed: a monitored device with a bogus address took its site's console offline.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(8);

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
    public int GetOrCreateEndpoint(string siteSlug, string host, int port, bool isConsole = false)
    {
        var listener = _listeners.GetOrAdd($"{siteSlug}|{host}:{port}", key =>
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var created = new ProxyListener(tcp, siteSlug, host, port, isConsole);
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

    /// <summary>
    /// A connection to {host}:{port} inside the given site, as a stream, with no loopback listener.
    /// The agent dials whatever address it is given, so a caller that follows a device's redirect
    /// with this stays inside that site. Throws when the agent refuses or cannot open the target.
    /// </summary>
    public async Task<Stream> OpenStreamAsync(string siteSlug, string host, int port, CancellationToken ct)
    {
        var (callerSide, proxySide) = DuplexPipeStream.CreatePair();
        (ProxyConnection? Connection, string? Error) opened;
        try
        {
            opened = await OpenAsync(new ProxyTarget(siteSlug, host, port, IsConsole: false, LocalPort: null), proxySide, ct);
        }
        catch
        {
            await callerSide.DisposeAsync();
            throw;
        }

        var (connection, error) = opened;
        if (connection == null)
        {
            await callerSide.DisposeAsync();
            throw new IOException($"{host}:{port} via the site's agent - {error}");
        }

        _ = PumpToAgentAsync(connection, _shutdown.Token);
        return callerSide;
    }

    private async Task HandleLocalConnectionAsync(ProxyListener listener, TcpClient client, CancellationToken ct)
    {
        try
        {
            var target = new ProxyTarget(listener.SiteSlug, listener.TargetHost, listener.TargetPort, listener.IsConsole, listener.LocalPort);
            var (connection, _) = await OpenAsync(target, client.GetStream(), ct);
            if (connection != null)
                await PumpToAgentAsync(connection, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Asks the site's agent to open the target and waits for its answer. On success the connection
    /// is registered and <paramref name="local"/> carries its bytes; otherwise <paramref name="local"/>
    /// is disposed and the reason returned.
    /// </summary>
    private async Task<(ProxyConnection? Connection, string? Error)> OpenAsync(
        ProxyTarget target, Stream local, CancellationToken ct)
    {
        var agent = _registry.GetForSite(target.SiteSlug).FirstOrDefault();
        if (agent == null)
        {
            _logger.LogDebug("Proxy connect to {Host}:{Port} refused - no agent online for site {Slug}",
                target.Host, target.Port, target.SiteSlug);
            local.Dispose();
            return (null, "no agent online for the site");
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
                target.Host, target.Port, agent.AgentId, silent.TotalSeconds, target.SiteSlug);
            local.Dispose();
            // Belt-and-braces with the watchdog's proactive flip: if a dial reaches
            // a stale tunnel before the watchdog's next 15s tick has flipped the
            // console, flip it now so this page's remaining calls short-circuit.
            FlipConsoleAwaitingAgent(target.SiteSlug);
            return (null, "the site's agent has gone silent");
        }

        // Circuit breaker: a recent open to this site timed out and no inbound has
        // arrived since, so the tunnel is almost certainly still black-holed.
        // Fast-fail the render's remaining opens rather than eat OpenTimeout on
        // each. Fresh inbound (LastMessageAt past when the breaker tripped) means
        // the tunnel recovered, clearing this without needing a probe.
        if (_openBreaker.TryGetValue(target.SiteSlug, out var breaker)
            && DateTime.UtcNow < breaker.Until
            && agent.LastMessageAt <= breaker.OpenedAtLastMsg)
        {
            _logger.LogDebug("Proxy connect to {Host}:{Port} fast-refused - open breaker tripped for agent {AgentId} (site {Slug})",
                target.Host, target.Port, agent.AgentId, target.SiteSlug);
            local.Dispose();
            return (null, "the site's agent is not answering");
        }

        var id = Interlocked.Increment(ref _nextConnectionId);
        var connection = new ProxyConnection(id, local, agent);
        _connections[id] = connection;
        try
        {
            var sent = await agent.SendAsync(new ServerMessage
            {
                ProxyOpen = new ProxyOpen { ConnectionId = id, Host = target.Host, Port = target.Port }
            }, ct);
            if (!sent)
            {
                CloseConnection(connection, notifyAgent: false);
                return (null, "the site's agent disconnected");
            }

            using var openTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            openTimeout.CancelAfter(OpenTimeout);
            string? openError;
            try
            {
                openError = await connection.OpenResult.Task.WaitAsync(openTimeout.Token);
            }
            // Only our own timeout counts against the tunnel. A caller giving up (a disposed client,
            // a cancelled poll) propagates instead, and the outer catch closes the connection.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                openError = "open timed out";
                // Trip the breaker so the opens queued behind this one fast-fail
                // instead of each blocking the full OpenTimeout.
                var wasTripped = _openBreaker.TryGetValue(target.SiteSlug, out var prev)
                                 && DateTime.UtcNow < prev.Until;
                _openBreaker[target.SiteSlug] = (DateTime.UtcNow + OpenBreakerHold, agent.LastMessageAt);

                // On the FIRST timeout of an outage, flip the site's console to
                // awaiting-agent now (not at the 90s watchdog), so its page renders
                // short-circuit console calls instead of each dialing the dead proxy
                // and paying the retry backoff.
                //
                // Only when the tunnel itself looks silent. An agent that has messaged
                // recently is demonstrably alive, so a target that did not answer says
                // nothing about the tunnel - and tearing the console down for it takes
                // the site offline over one unreachable device.
                var tunnelSilent = DateTime.UtcNow - agent.LastMessageAt > AgentTunnelConnection.StaleThreshold;
                if (!wasTripped && tunnelSilent)
                    FlipConsoleAwaitingAgent(target.SiteSlug);
            }
            if (openError != null)
            {
                _logger.LogDebug("Proxy open {Host}:{Port} via agent {AgentId} failed: {Error}",
                    target.Host, target.Port, agent.AgentId, openError);

                // The console's own endpoint failing IS the console being down - the flip above only
                // covers a silent tunnel. Two strikes so one blip can't mark a healthy console down.
                if (target.IsConsole
                    && _consoleOpenFailures.AddOrUpdate(target.SiteSlug, 1, (_, n) => n + 1) >= ConsoleFailuresBeforeUnreachable)
                {
                    NoteConsoleUnreachable(target.SiteSlug);
                }
                // The dialer only sees the socket close, so leave the agent's reason where they can
                // find it - SSH.NET reports the hang-up as a missing banner and buries the cause.
                if (target.LocalPort is { } localPort)
                {
                    _lastOpenFailure[localPort] =
                        ($"{target.Host}:{target.Port} via the site's agent - {openError}", DateTime.UtcNow);
                }
                CloseConnection(connection, notifyAgent: false);
                return (null, openError);
            }

            // The tunnel answered, so clear any open breaker for this site.
            if (target.IsConsole) _consoleOpenFailures.TryRemove(target.SiteSlug, out _);
            _openBreaker.TryRemove(target.SiteSlug, out _);
            if (target.LocalPort is { } openedPort) _lastOpenFailure.TryRemove(openedPort, out _);
            return (connection, null);
        }
        catch
        {
            CloseConnection(connection, notifyAgent: true);
            throw;
        }
    }

    /// <summary>
    /// Local reads -> tunnel, with backpressure, until either side closes. The agent-to-local
    /// direction is written by the tunnel read loop via OnProxyDataAsync.
    /// </summary>
    private async Task PumpToAgentAsync(ProxyConnection connection, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[FrameBytes];
            while (!ct.IsCancellationRequested)
            {
                int read;
                try { read = await connection.Local.ReadAsync(buffer, ct); }
                catch { break; }
                if (read <= 0) break;
                var ok = await connection.Agent.SendAsync(new ServerMessage
                {
                    ProxyData = new ProxyData { ConnectionId = connection.Id, Data = ByteString.CopyFrom(buffer, 0, read) }
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
    // Why the last open on a loopback listener failed, so a caller holding nothing but a closed
    // socket can say something useful. Short-lived on purpose: an old reason attached to an
    // unrelated later failure would be worse than no reason at all.
    private readonly ConcurrentDictionary<int, (string Reason, DateTime At)> _lastOpenFailure = new();
    private static readonly TimeSpan FailureReasonTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The agent's own reason for refusing or failing the last open on this loopback port, if it
    /// was recent. Null when the port is not one of ours, or nothing has failed on it lately.
    /// </summary>
    public string? RecentOpenFailure(int localPort)
    {
        if (!_lastOpenFailure.TryGetValue(localPort, out var failure)) return null;
        if (DateTime.UtcNow - failure.At > FailureReasonTtl)
        {
            _lastOpenFailure.TryRemove(localPort, out _);
            return null;
        }
        return failure.Reason;
    }

    public void OnProxyOpenResult(AgentTunnelConnection sender, ProxyOpenResult result)
    {
        if (TryGetOwnedConnection(sender, result.ConnectionId, out var connection))
            connection.OpenResult.TrySetResult(result.Ok ? null : (string.IsNullOrEmpty(result.Error) ? "connect failed" : result.Error));
    }

    /// <summary>
    /// Resolves a connection id only for the agent that owns it. Ids come from one counter
    /// shared by every agent, so without the owner check an enrolled agent could write into or
    /// close another site's proxied stream by naming its id.
    /// </summary>
    private bool TryGetOwnedConnection(AgentTunnelConnection sender, long connectionId, out ProxyConnection connection)
    {
        if (!_connections.TryGetValue(connectionId, out var found))
        {
            connection = null!;
            return false;
        }

        if (!ReferenceEquals(found.Agent, sender))
        {
            _logger.LogWarning(
                "Agent {AgentId} referenced proxy connection {ConnectionId}, which belongs to agent {OwnerAgentId}; ignoring",
                sender.AgentId, connectionId, found.Agent.AgentId);
            connection = null!;
            return false;
        }

        connection = found;
        return true;
    }

    /// <summary>
    /// Agent-to-local bytes. Called sequentially from the tunnel read loop, so
    /// writes stay ordered; a wedged local reader can stall that loop, which
    /// is the accepted trade-off of the single-stream design.
    /// </summary>
    public async Task OnProxyDataAsync(AgentTunnelConnection sender, ProxyData data, CancellationToken ct)
    {
        if (!TryGetOwnedConnection(sender, data.ConnectionId, out var connection)) return;
        try
        {
            await connection.Local.WriteAsync(data.Data.Memory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CloseConnection(connection, notifyAgent: true);
        }
    }

    /// <summary>Agent reports the site-side socket closed.</summary>
    public void OnProxyClose(AgentTunnelConnection sender, ProxyClose close)
    {
        if (TryGetOwnedConnection(sender, close.ConnectionId, out var connection))
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

    /// <summary>Consecutive failed opens on a site's console endpoint before it counts as down.</summary>
    private const int ConsoleFailuresBeforeUnreachable = 2;
    private readonly ConcurrentDictionary<string, int> _consoleOpenFailures = new();

    /// <summary>
    /// The agent is alive but cannot reach the console, so this is not awaiting-agent. Marks the
    /// console down (fire-and-forget, idempotent) so renders stop paying an open per call.
    /// </summary>
    private void NoteConsoleUnreachable(string siteSlug)
    {
        _ = Task.Run(async () =>
        {
            try { await _siteConnections.GetFor(siteSlug).NoteConsoleUnreachableAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Console-unreachable flip failed for site {Slug}", siteSlug); }
        });
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
        try { connection.Local.Dispose(); } catch { }
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

    private sealed record ProxyTarget(string SiteSlug, string Host, int Port, bool IsConsole, int? LocalPort);

    private sealed record ProxyListener(TcpListener Tcp, string SiteSlug, string TargetHost, int TargetPort, bool IsConsole = false)
    {
        public int LocalPort => ((IPEndPoint)Tcp.LocalEndpoint).Port;
    }

    private sealed class ProxyConnection
    {
        public ProxyConnection(long id, Stream local, AgentTunnelConnection agent)
        {
            Id = id;
            Local = local;
            Agent = agent;
        }

        public long Id { get; }
        /// <summary>This end of the connection, disposed when it closes: a loopback socket's stream, or the proxy side of a stream pair.</summary>
        public Stream Local { get; }

        public AgentTunnelConnection Agent { get; }

        /// <summary>Null = opened; otherwise the failure reason.</summary>
        public TaskCompletionSource<string?> OpenResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

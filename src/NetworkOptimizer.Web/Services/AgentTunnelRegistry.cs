using System.Collections.Concurrent;
using System.Threading.Channels;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Tracks live agent tunnel connections. Each connected agent gets an outbound
/// message channel that the tunnel handler drains to its gRPC response stream,
/// so any server code can push a message to a connected agent without touching
/// the stream directly. One connection per agent: a reconnect replaces (and
/// completes) the previous connection's channel.
/// </summary>
public class AgentTunnelRegistry
{
    private readonly ConcurrentDictionary<int, AgentTunnelConnection> _connections = new();

    // Last time each agent had an open tunnel (stamped on connect and on drop).
    // Bridges the brief gap while a previously-connected tunnel reconnects, so a
    // real reconnect doesn't flap the online status - without letting a tunnel
    // that never connected count as live.
    private readonly ConcurrentDictionary<int, DateTime> _lastTunnelActivity = new();

    // Whether this server bound the agent tunnel listener. When it did, an agent
    // that only ever REST-heartbeats has a dead or unpublished tunnel (e.g. the
    // gRPC path isn't reverse-proxied) and its tunnel-dependent features are all
    // down, so heartbeat freshness alone must NOT read as online.
    private readonly bool _tunnelEnabled;

    // How long after a tunnel drops an agent still counts as live, covering the
    // agent's dial-out backoff so a genuine reconnect doesn't blink the status
    // offline. Sized just over the agent heartbeat interval (30s) plus a dial
    // attempt; a tunnel down longer than this is a real outage, not a reconnect.
    private static readonly TimeSpan TunnelReconnectGrace = TimeSpan.FromSeconds(75);

    public AgentTunnelRegistry(AgentTunnelOptions tunnelOptions)
    {
        _tunnelEnabled = tunnelOptions.Enabled;
    }

    /// <summary>Registers a new live connection, displacing any stale one for the same agent.</summary>
    public AgentTunnelConnection Register(int agentId, string siteSlug, string agentName)
    {
        var connection = new AgentTunnelConnection(agentId, siteSlug, agentName);
        _connections.AddOrUpdate(agentId, connection, (_, old) =>
        {
            old.Complete();
            return connection;
        });
        _lastTunnelActivity[agentId] = DateTime.UtcNow;
        return connection;
    }

    /// <summary>
    /// Removes a connection if it is still the current one for its agent.
    /// A reconnect may already have replaced it; in that case this is a no-op.
    /// </summary>
    public void Unregister(AgentTunnelConnection connection)
    {
        connection.Complete();
        // Stamp the drop time so the reconnect grace is measured from when the
        // tunnel actually went away, not from when it first connected.
        _lastTunnelActivity[connection.AgentId] = DateTime.UtcNow;
        ((ICollection<KeyValuePair<int, AgentTunnelConnection>>)_connections)
            .Remove(new KeyValuePair<int, AgentTunnelConnection>(connection.AgentId, connection));
    }

    /// <summary>Whether the agent currently holds an open tunnel.</summary>
    public bool IsConnected(int agentId) => _connections.ContainsKey(agentId);

    /// <summary>
    /// Whether an agent counts as online for status displays. An open tunnel is
    /// authoritative and instant. When this server offers no tunnel at all, a
    /// fresh REST heartbeat is the best signal and keeps the agent online. But
    /// when the server DOES offer a tunnel, a heartbeat-only agent has a dead or
    /// unpublished tunnel (e.g. the gRPC path isn't reverse-proxied) with every
    /// tunnel-dependent feature down - reporting it online off REST heartbeats
    /// alone would be dishonest - so only an open tunnel, or the brief grace
    /// while a previously-connected one reconnects, counts. The single definition
    /// every status surface (site dropdown, All Sites, Multi-Site settings)
    /// shares, so they can't disagree on what "online" means.
    /// </summary>
    public bool IsAgentLive(NetworkOptimizer.Storage.Models.SiteAgent agent)
    {
        if (IsConnected(agent.Id))
            return true;
        if (_tunnelEnabled)
            return _lastTunnelActivity.TryGetValue(agent.Id, out var last)
                && DateTime.UtcNow - last < TunnelReconnectGrace;
        return AgentEnrollmentService.IsOnline(agent.LastSeenAt);
    }

    /// <summary>
    /// Whether we've heard from the agent recently by any means - open tunnel or a
    /// fresh REST heartbeat. Deliberately looser than <see cref="IsAgentLive"/>,
    /// which requires a working tunnel: this is the reachability signal used to
    /// pick a site's LAN speed-test target, since that test hits the agent's own
    /// nginx directly rather than the tunnel, so a heartbeat-only agent is still a
    /// candidate. It does NOT verify the agent actually hosts a speed test (LAN
    /// testing is an opt-in agent flag the server has no signal for yet); the
    /// resolver's on-gateway check is the only capability gate today.
    /// </summary>
    public bool IsReachable(NetworkOptimizer.Storage.Models.SiteAgent agent) =>
        IsConnected(agent.Id) || AgentEnrollmentService.IsOnline(agent.LastSeenAt);

    /// <summary>Live connections for a site (normally zero or one per agent).</summary>
    public List<AgentTunnelConnection> GetForSite(string siteSlug) =>
        _connections.Values.Where(c => c.SiteSlug == siteSlug).ToList();

    /// <summary>All live connections across sites.</summary>
    public List<AgentTunnelConnection> GetAll() => _connections.Values.ToList();

    /// <summary>
    /// Queues a message for a connected agent. Returns false when the agent has
    /// no open tunnel (callers treat that as "will get config on next connect").
    /// </summary>
    public bool TrySend(int agentId, ServerMessage message) =>
        _connections.TryGetValue(agentId, out var connection) && connection.TrySend(message);
}

/// <summary>One live agent tunnel. Created by the registry, drained by the tunnel handler.</summary>
public sealed class AgentTunnelConnection
{
    // Wait (not drop) when full: proxy byte streams ride this channel, and
    // dropping a frame would corrupt them. Proxy senders use SendAsync for
    // real backpressure; TrySend (config pushes) may fail when the channel is
    // full or completed, which is fine - the periodic refresh retries.
    private readonly Channel<ServerMessage> _outbound = Channel.CreateBounded<ServerMessage>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });

    internal AgentTunnelConnection(int agentId, string siteSlug, string agentName)
    {
        AgentId = agentId;
        SiteSlug = siteSlug;
        AgentName = agentName;
        ConnectedAt = DateTime.UtcNow;
        LastMessageAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Port this agent serves its LAN speed test page on, as announced in its hello. Zero for an
    /// agent old enough not to say, which the callers read as the 3000 those agents serve. Lives on
    /// the connection rather than the agent row because it only means anything while the agent is
    /// connected - a target is never composed for an offline one.
    /// </summary>
    public int SpeedTestPort { get; internal set; }

    public int AgentId { get; }
    public string SiteSlug { get; }
    public string AgentName { get; }
    public DateTime ConnectedAt { get; }
    public DateTime LastMessageAt { get; internal set; }

    /// <summary>
    /// How long a tunnel may go silent before it counts as black-holed (agents
    /// heartbeat every 30s and the server re-pushes configs every 60s, so a
    /// healthy tunnel is never this quiet). A black-holed tunnel stays
    /// REGISTERED until the 90s watchdog reaps it, so every consumer that asks
    /// "is the agent there?" must use <see cref="IsStale"/> rather than mere
    /// registration - the proxy's open gate, the console connect/wait paths,
    /// and the config-refresh reconnect guard all share this single definition
    /// so they can't disagree about the dead-but-registered window.
    /// </summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(45);

    /// <summary>True when nothing has arrived past <see cref="StaleThreshold"/>: dead-but-registered.</summary>
    public bool IsStale => DateTime.UtcNow - LastMessageAt > StaleThreshold;

    private readonly CancellationTokenSource _dropCts = new();

    /// <summary>Cancelled when the server force-drops this connection (license enforcement).</summary>
    internal CancellationToken DropToken => _dropCts.Token;

    /// <summary>
    /// Force-terminates the tunnel from the server side: cancels the handler's
    /// read/write loops and completes the outbound channel. The agent's own
    /// dial-out backoff governs reconnect attempts.
    /// </summary>
    internal void Drop()
    {
        try { _dropCts.Cancel(); } catch (ObjectDisposedException) { }
        Complete();
    }

    /// <summary>Server-to-agent messages awaiting the stream pump.</summary>
    internal ChannelReader<ServerMessage> Outbound => _outbound.Reader;

    internal bool TrySend(ServerMessage message) => _outbound.Writer.TryWrite(message);

    /// <summary>Queues with backpressure. False once the connection is torn down.</summary>
    internal async ValueTask<bool> SendAsync(ServerMessage message, CancellationToken ct)
    {
        try
        {
            await _outbound.Writer.WriteAsync(message, ct);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    internal void Complete() => _outbound.Writer.TryComplete();
}

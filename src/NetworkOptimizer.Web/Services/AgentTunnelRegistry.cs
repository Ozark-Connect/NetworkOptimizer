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

    /// <summary>Registers a new live connection, displacing any stale one for the same agent.</summary>
    public AgentTunnelConnection Register(int agentId, string siteSlug, string agentName)
    {
        var connection = new AgentTunnelConnection(agentId, siteSlug, agentName);
        _connections.AddOrUpdate(agentId, connection, (_, old) =>
        {
            old.Complete();
            return connection;
        });
        return connection;
    }

    /// <summary>
    /// Removes a connection if it is still the current one for its agent.
    /// A reconnect may already have replaced it; in that case this is a no-op.
    /// </summary>
    public void Unregister(AgentTunnelConnection connection)
    {
        connection.Complete();
        ((ICollection<KeyValuePair<int, AgentTunnelConnection>>)_connections)
            .Remove(new KeyValuePair<int, AgentTunnelConnection>(connection.AgentId, connection));
    }

    /// <summary>Whether the agent currently holds an open tunnel.</summary>
    public bool IsConnected(int agentId) => _connections.ContainsKey(agentId);

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
    private readonly Channel<ServerMessage> _outbound = Channel.CreateBounded<ServerMessage>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    internal AgentTunnelConnection(int agentId, string siteSlug, string agentName)
    {
        AgentId = agentId;
        SiteSlug = siteSlug;
        AgentName = agentName;
        ConnectedAt = DateTime.UtcNow;
        LastMessageAt = DateTime.UtcNow;
    }

    public int AgentId { get; }
    public string SiteSlug { get; }
    public string AgentName { get; }
    public DateTime ConnectedAt { get; }
    public DateTime LastMessageAt { get; internal set; }

    /// <summary>Server-to-agent messages awaiting the stream pump.</summary>
    internal ChannelReader<ServerMessage> Outbound => _outbound.Reader;

    internal bool TrySend(ServerMessage message) => _outbound.Writer.TryWrite(message);

    internal void Complete() => _outbound.Writer.TryComplete();
}

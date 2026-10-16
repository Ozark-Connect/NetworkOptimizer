using System.Collections.Concurrent;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Threats.Blocking;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Sends Automatic Blocking's gateway set to the On-Site Agents running on each site's gateway, and
/// tracks what each one reports it has applied. The blocking cycle pushes every minute; a set goes
/// out when an agent does not hold it yet, when it connects, and every few minutes regardless, which
/// renews the public list entries' time to live.
/// </summary>
public sealed class GatewayAgentBlocklist : IGatewayAgentBlocklist
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "gateway-blocklist";

    private static readonly TimeSpan ResendEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AckWait = TimeSpan.FromMinutes(2);

    private readonly AgentTunnelRegistry _registry;
    private readonly ILogger<GatewayAgentBlocklist> _logger;
    // The set each site should hold, keyed by site slug.
    private readonly ConcurrentDictionary<string, Desired> _desired = new();
    // When each agent was last sent a set, and which one.
    private readonly ConcurrentDictionary<int, (string Fingerprint, DateTime At)> _sent = new();
    // What each agent last reported.
    private readonly ConcurrentDictionary<int, GatewayBlocklistStatus> _status = new();

    private sealed record Desired(bool Enabled, IReadOnlyList<GatewayAgentEntry> Entries, IReadOnlyList<GatewayAgentEntry> Outbound, string Fingerprint);

    // Resolved on first use, never injected: the collection service depends (through the list lookup)
    // on the blocking service, which depends on this one, and a constructor cycle hangs startup.
    private readonly IServiceProvider _services;

    public GatewayAgentBlocklist(AgentTunnelRegistry registry, ILogger<GatewayAgentBlocklist> logger, IServiceProvider services)
    {
        _registry = registry;
        _logger = logger;
        _services = services;
    }

    private static string Slug(string? site) => string.IsNullOrEmpty(site) ? SiteManagementService.DefaultSiteSlug : site;

    private List<AgentTunnelConnection> Capable(string slug) =>
        _registry.GetForSite(slug).Where(c => c.OnGateway == true && c.HasCapability(Capability) && !c.IsStale).ToList();

    public bool Push(string? site, bool enabled, IReadOnlyList<GatewayAgentEntry> entries, IReadOnlyList<GatewayAgentEntry> outbound,
        string fingerprint)
    {
        var slug = Slug(site);
        var desired = new Desired(enabled, entries, outbound, fingerprint);
        _desired[slug] = desired;
        var agents = Capable(slug);
        var now = DateTime.UtcNow;
        foreach (var agent in agents)
        {
            var applied = _status.TryGetValue(agent.AgentId, out var status) && status.Fingerprint == fingerprint && status.Ok;
            var sent = _sent.TryGetValue(agent.AgentId, out var last);
            if (applied && sent && last.Fingerprint == fingerprint && now - last.At < ResendEvery) continue;
            // An unanswered send is repeated each cycle only after a minute's wait, not on every call.
            if (!applied && sent && last.Fingerprint == fingerprint && now - last.At < TimeSpan.FromSeconds(55)) continue;
            Send(agent, desired, now);
        }
        return agents.Count > 0;
    }

    // An agent whose last report was good still counts while a newer set is on its way: its ack lands
    // a second after the push, so requiring the current fingerprint would never count a busy site.
    public bool IsEnforcing(string? site, string fingerprint) =>
        Capable(Slug(site)).Any(agent => _status.TryGetValue(agent.AgentId, out var s) && s.Ok && s.HookPresent
            && (s.Fingerprint == fingerprint
                || (_sent.TryGetValue(agent.AgentId, out var sent) && sent.Fingerprint == fingerprint
                    && DateTime.UtcNow - sent.At < AckWait)));

    public string? Problem(string? site, string fingerprint)
    {
        var now = DateTime.UtcNow;
        foreach (var agent in Capable(Slug(site)))
        {
            if (_status.TryGetValue(agent.AgentId, out var s) && !s.Ok)
                return string.IsNullOrWhiteSpace(s.Error) ? "Blocklist not applied" : s.Error;
            if (s is { HookPresent: false })
                return "Rules not in place";
            var awaited = _sent.TryGetValue(agent.AgentId, out var sent) && now - sent.At >= AckWait;
            if (s?.Fingerprint != fingerprint && awaited)
                return "Not confirming the current blocklist";
        }
        return null;
    }

    /// <summary>Sends a newly connected gateway agent its site's set, if there is one yet.</summary>
    public void OnAgentConnected(AgentTunnelConnection connection)
    {
        _sent.TryRemove(connection.AgentId, out _);
        _status.TryRemove(connection.AgentId, out _);
        if (connection.OnGateway != true || !connection.HasCapability(Capability)) return;
        if (_desired.TryGetValue(connection.SiteSlug, out var desired))
            Send(connection, desired, DateTime.UtcNow);
    }

    public void OnAgentDisconnected(AgentTunnelConnection connection)
    {
        _sent.TryRemove(connection.AgentId, out _);
        _status.TryRemove(connection.AgentId, out _);
    }

    /// <summary>
    /// Hands the devices an agent saw trying to reach an outbound-blocked address to Threat Intelligence,
    /// which tags, de-duplicates, and alerts on them with the site's next collection.
    /// </summary>
    public void OnHits(AgentTunnelConnection connection, GatewayBlocklistHits hits)
    {
        // Only the agent that enforces the blocklist can have seen its drops; any other agent of the
        // site sending them would be writing Threat events and alerts it has no business raising.
        if (connection.OnGateway != true || !connection.HasCapability(Capability))
        {
            _logger.LogWarning("Ignored gateway blocklist hits from agent {Agent} ({Site}): not a gateway agent with the blocklist",
                connection.AgentName, connection.SiteSlug);
            return;
        }
        if (hits.Hits.Count == 0 || _services.GetService<NetworkOptimizer.Threats.ThreatCollectionService>() is not { } collection) return;
        var drops = hits.Hits
            .Where(h => !string.IsNullOrWhiteSpace(h.ClientIp) && !string.IsNullOrWhiteSpace(h.DestIp))
            .Select(h => new NetworkOptimizer.Threats.Models.ThreatEvent
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(h.LastUnixMs).UtcDateTime,
                SourceIp = h.ClientIp,
                SourceMac = string.IsNullOrEmpty(h.ClientMac) ? null : h.ClientMac,
                DestIp = h.DestIp,
                DestPort = h.DestPort,
                Protocol = h.Protocol,
                Action = NetworkOptimizer.Threats.Models.ThreatAction.Blocked,
                EventSource = NetworkOptimizer.Threats.Models.EventSource.TrafficFlow,
                Direction = "outgoing",
                Category = PublicListContact.Category,
                SignatureName = "",
                InnerAlertId = $"gateway-agent:{h.ClientIp}:{h.DestIp}:{h.Protocol}:{h.DestPort}:{h.FirstUnixMs}",
            })
            .ToList();
        collection.QueueGatewayDrops(connection.SiteSlug, drops);
    }

    /// <summary>Records an agent's report of what it applied.</summary>
    public void OnStatus(AgentTunnelConnection connection, GatewayBlocklistStatus status)
    {
        var previous = _status.TryGetValue(connection.AgentId, out var p) ? p : null;
        _status[connection.AgentId] = status;
        if (!status.Ok)
            _logger.LogWarning("Gateway blocklist on agent {Agent} ({Site}) failed: {Error}", connection.AgentName, connection.SiteSlug, status.Error);
        else if (!status.HookPresent)
            _logger.LogWarning("Gateway blocklist on agent {Agent} ({Site}): its rules are not in place", connection.AgentName, connection.SiteSlug);
        else if (previous?.Fingerprint != status.Fingerprint)
            _logger.LogInformation("Gateway blocklist on agent {Agent} ({Site}): {V4} IPv4 and {V6} IPv6 entries applied",
                connection.AgentName, connection.SiteSlug, status.V4Entries, status.V6Entries);
    }

    private static IEnumerable<GatewayBlockEntry> Wire(IEnumerable<GatewayAgentEntry> entries, DateTime now) => entries
        .Select(e => (e.Address, Ttl: (e.ExpiresAt - now).TotalSeconds))
        .Where(e => e.Ttl >= 1)
        .Select(e => new GatewayBlockEntry { Address = e.Address, TtlSeconds = (int)Math.Min(int.MaxValue, Math.Ceiling(e.Ttl)) });

    private void Send(AgentTunnelConnection agent, Desired desired, DateTime now)
    {
        var message = new GatewayBlocklistSet { Enabled = desired.Enabled, Fingerprint = desired.Fingerprint };
        message.Entries.AddRange(Wire(desired.Entries, now));
        message.Outbound.AddRange(Wire(desired.Outbound, now));
        if (_registry.TrySend(agent.AgentId, new ServerMessage { GatewayBlocklist = message }))
            _sent[agent.AgentId] = (desired.Fingerprint, now);
    }
}

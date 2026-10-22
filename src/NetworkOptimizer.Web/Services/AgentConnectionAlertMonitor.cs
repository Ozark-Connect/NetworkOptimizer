using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Periodically sweeps enrolled agents and raises alert events when one stays
/// offline past the grace threshold, pairing each with a reconnect event when it
/// comes back. Sweeps <see cref="AgentTunnelRegistry.IsAgentLive"/> (open tunnel,
/// its brief reconnect grace, or - only when this server offers no tunnel - REST
/// heartbeat freshness) instead of hooking tunnel teardown, so agent redeploys
/// and transient tunnel bounces never alert - only an agent continuously offline
/// for the full threshold does.
/// </summary>
public class AgentConnectionAlertMonitor : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(3);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly IAlertEventBus _alertBus;
    private readonly ILogger<AgentConnectionAlertMonitor> _logger;

    // Per-agent sweep state. Only touched by the sweep loop, no locking needed.
    private sealed class AgentState
    {
        public DateTime OfflineSince;
        public bool Alerted;
        // The subject the offline alert used, so the reconnect names the agent the same way.
        public string? Subject;
    }

    private readonly Dictionary<int, AgentState> _states = new();

    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly AgentOnGatewayDetector _onGateway;

    public AgentConnectionAlertMonitor(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        AgentTunnelRegistry tunnelRegistry,
        IAlertEventBus alertBus,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        AgentOnGatewayDetector onGateway,
        ILogger<AgentConnectionAlertMonitor> logger)
    {
        _mainDbFactory = mainDbFactory;
        _tunnelRegistry = tunnelRegistry;
        _alertBus = alertBus;
        _siteWorkGate = siteWorkGate;
        _onGateway = onGateway;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent connection alert sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(ct);

        // Only agents that completed enrollment and have contacted us at least
        // once can go "offline" - a freshly enrolled agent that never connected
        // is a setup in progress, not an outage. Disabled sites are excluded:
        // taking a paused site's agent down is intentional, not an outage.
        var agents = await db.SiteAgents.AsNoTracking()
            .Where(a => a.Enabled && a.EnrolledAt != null && a.LastSeenAt != null)
            .Join(db.Sites.AsNoTracking().Where(s => s.Enabled), a => a.SiteId, s => s.Id,
                (a, s) => new { Agent = a, s.Slug })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var seen = new HashSet<int>();
        var live = agents.ToDictionary(e => e.Agent.Id, e => _tunnelRegistry.IsAgentLive(e.Agent));

        foreach (var entry in agents)
        {
            var agent = entry.Agent;
            seen.Add(agent.Id);

            // Licensing force-drops a non-operational site's tunnel, so alerting on that would be
            // wrong twice: it is noise, and it reports an outage the instance itself caused. Held in
            // `seen` so the agent keeps its state row and does not re-alert as new on restore.
            if (!_siteWorkGate.IsSiteOperational(entry.Slug))
                continue;

            if (live[agent.Id])
            {
                if (_states.Remove(agent.Id, out var prior) && prior.Alerted)
                {
                    await PublishAsync("agent.reconnected", AlertSeverity.Info,
                        "On-Site Agent reconnected",
                        $"{prior.Subject ?? AgentLabel(agent.Name)} is back online.",
                        agent, entry.Slug, ct);
                }
                continue;
            }

            if (!_states.TryGetValue(agent.Id, out var state))
            {
                _states[agent.Id] = new AgentState { OfflineSince = now };
                continue;
            }

            if (!state.Alerted && now - state.OfflineSince >= OfflineThreshold)
            {
                state.Alerted = true;
                var lastSeen = agent.LastSeenAt.HasValue
                    ? $"Last seen {agent.LastSeenAt:u}."
                    : "";
                var minutes = (int)OfflineThreshold.TotalMinutes;

                // Any live agent on the site carries its tunnel traffic, so with another one up only
                // what runs on this agent is lost. Everything else reads as a whole-site outage.
                var othersLive = agents.Count(e => e.Slug == entry.Slug && e.Agent.Id != agent.Id && live[e.Agent.Id]);
                string message;
                if (othersLive == 0)
                {
                    state.Subject = AgentLabel(agent.Name);
                    message = $"{state.Subject} has been offline for over {minutes} minutes. Its site's monitoring, speed tests, and Console connection are unavailable until it reconnects.";
                }
                else
                {
                    var onGateway = await IsOnGatewayAsync(agent, entry.Slug, ct);
                    state.Subject = AgentSubject(agent.Name, onGateway, agent.LanIp);
                    var keeps = othersLive == 1 ? "other Agent keeps" : "other Agents keep";
                    var runsOn = onGateway ? "the Gateway Agent" : "this Agent";
                    message = $"{state.Subject} has been offline for over {minutes} minutes. This site's {keeps} the UniFi Console connection up. Monitoring and speed tests that run on {runsOn} are unavailable until it reconnects.";
                }

                await PublishAsync("agent.offline", AlertSeverity.Warning,
                    "On-Site Agent offline",
                    $"{message} {lastSeen}",
                    agent, entry.Slug, ct);
            }
        }

        // Forget agents that were deleted or disabled mid-outage.
        foreach (var staleId in _states.Keys.Where(id => !seen.Contains(id)).ToList())
            _states.Remove(staleId);
    }

    /// <summary>
    /// Subject phrase for an agent in alert copy. An agent with no distinctive name (a default
    /// "Agent" / "Agent 2", or nothing at all) would otherwise render the redundant
    /// Agent "Agent" - fall back to a plain reference. A named agent keeps its quoted name.
    /// </summary>
    internal static string AgentLabel(string? name) =>
        IsDefaultName(name) ? "The On-Site Agent" : $"Agent \"{name!.Trim()}\"";

    /// <summary>
    /// Subject phrase for an agent on a site with more than one agent, where the reader has to know
    /// which one is down: the Gateway Agent by its role, any other by its LAN IP. A distinctive name
    /// is added in quotes.
    /// </summary>
    internal static string AgentSubject(string? name, bool onGateway, string? lanIp)
    {
        var quoted = IsDefaultName(name) ? "" : $" \"{name!.Trim()}\"";
        if (onGateway)
            return $"The Gateway Agent{quoted}";
        return string.IsNullOrWhiteSpace(lanIp)
            ? $"The On-Site Agent{quoted}"
            : $"The On-Site Agent{quoted} on {lanIp}";
    }

    // Enrollment names agents "Agent", "Agent 2", and so on; none of those tells the reader anything.
    private static bool IsDefaultName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return true;
        if (!trimmed.StartsWith("agent", StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = trimmed[5..].Trim();
        return suffix.Length == 0 || suffix.All(char.IsAsciiDigit);
    }

    private async Task<bool> IsOnGatewayAsync(SiteAgent agent, string siteSlug, CancellationToken ct)
    {
        IReadOnlyList<string> candidates = string.IsNullOrEmpty(agent.LanIp) ? [] : [agent.LanIp];
        try
        {
            return await _onGateway.IsAgentOnGatewayAsync(siteSlug, agent.Id, candidates, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "On-gateway check failed for agent {Id}; alerting without it", agent.Id);
            return false;
        }
    }

    private async Task PublishAsync(
        string eventType, AlertSeverity severity, string title, string message,
        SiteAgent agent, string siteSlug, CancellationToken ct)
    {
        try
        {
            await _alertBus.PublishAsync(new AlertEvent
            {
                EventType = eventType,
                Source = "agent",
                Severity = severity,
                Title = title,
                Message = message,
                DeviceName = agent.Name,
                DeviceIp = agent.LanIp,
                SiteSlug = siteSlug == SiteManagementService.DefaultSiteSlug ? null : siteSlug
            }, ct);
            _logger.LogInformation("Published {EventType} for agent {Name} (site {Site})",
                eventType, agent.Name, siteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish {EventType} for agent {Name}", eventType, agent.Name);
        }
    }
}

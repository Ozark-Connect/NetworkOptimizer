using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Periodically sweeps enrolled agents and raises alert events when one stays
/// offline past the grace threshold, pairing each with a reconnect event when it
/// comes back. Sweeps <see cref="AgentTunnelRegistry.IsAgentLive"/> (open tunnel,
/// or heartbeat freshness for REST-only agents and reconnect gaps) instead of
/// hooking tunnel teardown, so agent redeploys and transient tunnel bounces never
/// alert - only an agent continuously offline for the full threshold does.
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
    }

    private readonly Dictionary<int, AgentState> _states = new();

    public AgentConnectionAlertMonitor(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        AgentTunnelRegistry tunnelRegistry,
        IAlertEventBus alertBus,
        ILogger<AgentConnectionAlertMonitor> logger)
    {
        _mainDbFactory = mainDbFactory;
        _tunnelRegistry = tunnelRegistry;
        _alertBus = alertBus;
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

        foreach (var entry in agents)
        {
            var agent = entry.Agent;
            seen.Add(agent.Id);

            if (_tunnelRegistry.IsAgentLive(agent))
            {
                if (_states.Remove(agent.Id, out var prior) && prior.Alerted)
                {
                    await PublishAsync("agent.reconnected", AlertSeverity.Info,
                        "On-Site Agent reconnected",
                        $"{AgentLabel(agent.Name)} is back online.",
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
                await PublishAsync("agent.offline", AlertSeverity.Warning,
                    "On-Site Agent offline",
                    $"{AgentLabel(agent.Name)} has been offline for over {(int)OfflineThreshold.TotalMinutes} minutes. Its site's monitoring, speed tests, and console connection are unavailable until it reconnects. {lastSeen}",
                    agent, entry.Slug, ct);
            }
        }

        // Forget agents that were deleted or disabled mid-outage.
        foreach (var staleId in _states.Keys.Where(id => !seen.Contains(id)).ToList())
            _states.Remove(staleId);
    }

    /// <summary>
    /// Subject phrase for an agent in alert copy. An agent with no distinctive name (the
    /// literal default "agent", or nothing at all) would otherwise render the redundant
    /// Agent "Agent" - fall back to a plain reference. A named agent keeps its quoted name.
    /// </summary>
    internal static string AgentLabel(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "agent", StringComparison.OrdinalIgnoreCase)
            ? "The On-Site Agent"
            : $"Agent \"{trimmed}\"";
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

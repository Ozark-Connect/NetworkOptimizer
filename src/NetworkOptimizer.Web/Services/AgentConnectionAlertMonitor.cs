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
/// for the full threshold does. A tunnel that keeps reconnecting inside the grace raises a separate
/// unstable alert instead, since it never reads as offline.
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

    // Offline alert times per agent within ChronicWindow, and the last unstable alert per agent.
    // Sweep loop only, like _states.
    private readonly Dictionary<int, List<DateTime>> _offlineAlerts = new();
    private readonly Dictionary<int, DateTime> _unstableAlertedAt = new();
    private static readonly TimeSpan ChronicWindow = TimeSpan.FromHours(24);
    private const int ChronicOfflineAlerts = 3;
    private static readonly TimeSpan UnstableRealert = TimeSpan.FromHours(24);

    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly AgentOnGatewayDetector _onGateway;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly IServiceScopeFactory? _scopes;

    private const string OfflineEventType = "agent.offline";
    private const string ReconnectedEventType = "agent.reconnected";

    // Agents still offline before a restart, read once per site (alerts live in each site's database).
    private readonly Dictionary<string, Monitoring.OpenAlertSeed> _openAtStart = new(StringComparer.OrdinalIgnoreCase);

    public AgentConnectionAlertMonitor(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        AgentTunnelRegistry tunnelRegistry,
        IAlertEventBus alertBus,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        AgentOnGatewayDetector onGateway,
        SiteTunnelRouting tunnelRouting,
        ILogger<AgentConnectionAlertMonitor> logger,
        IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
        _mainDbFactory = mainDbFactory;
        _tunnelRegistry = tunnelRegistry;
        _alertBus = alertBus;
        _siteWorkGate = siteWorkGate;
        _onGateway = onGateway;
        _tunnelRouting = tunnelRouting;
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

            // Still offline before a restart: start alerted, so its reconnect is published.
            if (!_states.ContainsKey(agent.Id)
                && (await OpenAtStart(entry.Slug).OpenAsync()).Take(OfflineEventType, agent.Id.ToString()) is { } stillOffline)
            {
                _states[agent.Id] = new AgentState
                {
                    OfflineSince = stillOffline.TriggeredAt,
                    Alerted = true,
                    Subject = AgentLabel(agent.Name)
                };
            }

            // A tunnel that reconnects inside the grace never reads as offline, so it needs its own alert.
            if (_tunnelRegistry.IsUnstable(agent.Id)
                && (!_unstableAlertedAt.TryGetValue(agent.Id, out var lastUnstable) || now - lastUnstable >= UnstableRealert))
            {
                _unstableAlertedAt[agent.Id] = now;
                var others = agents.Count(e => e.Slug == entry.Slug && e.Agent.Id != agent.Id);
                var subject = others == 0
                    ? AgentLabel(agent.Name)
                    : AgentSubject(agent.Name, await IsOnGatewayAsync(agent, entry.Slug, ct), agent.LanIp);
                var reconnects = _tunnelRegistry.ReconnectsWithin(agent.Id, AgentTunnelRegistry.UnstableWindow);
                _logger.LogWarning("Agent {Name} (site {Site}) reconnected {Count} times in {Minutes} minutes",
                    agent.Name, entry.Slug, reconnects, (int)AgentTunnelRegistry.UnstableWindow.TotalMinutes);
                await PublishAsync("agent.unstable", AlertSeverity.Warning,
                    "On-Site Agent connection unstable",
                    $"{subject} keeps dropping its connection. {_tunnelRegistry.DescribeUnstable(agent.Id)} {ProxyHelp}",
                    agent, entry.Slug, ct);
            }

            if (live[agent.Id])
            {
                if (_states.Remove(agent.Id, out var prior) && prior.Alerted)
                {
                    await PublishAsync(ReconnectedEventType, AlertSeverity.Info,
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
                // The Console is only mentioned when the site reaches it through the tunnel at all.
                var othersLive = agents.Count(e => e.Slug == entry.Slug && e.Agent.Id != agent.Id && live[e.Agent.Id]);
                var viaAgent = await _tunnelRouting.IsViaAgentAsync(entry.Slug);
                string message;
                if (othersLive == 0)
                {
                    state.Subject = AgentLabel(agent.Name);
                    var lost = viaAgent ? "monitoring, speed tests, and Console connection are" : "monitoring and speed tests are";
                    message = $"{state.Subject} has been offline for over {minutes} minutes. Its site's {lost} unavailable until it reconnects.";
                }
                else
                {
                    var onGateway = await IsOnGatewayAsync(agent, entry.Slug, ct);
                    state.Subject = AgentSubject(agent.Name, onGateway, agent.LanIp);
                    var keeps = othersLive == 1 ? "other Agent keeps" : "other Agents keep";
                    var console = viaAgent ? $" This site's {keeps} the UniFi Console connection up." : "";
                    var runsOn = onGateway ? "the Gateway Agent" : "this Agent";
                    message = $"{state.Subject} has been offline for over {minutes} minutes.{console} Monitoring and speed tests that run on {runsOn} are unavailable until it reconnects.";
                }

                var priorAlerts = RecordOfflineAlert(agent.Id, now);
                var hint = ChronicHint(priorAlerts + 1, _tunnelRegistry.IsUnstable(agent.Id));

                await PublishAsync(OfflineEventType, AlertSeverity.Warning,
                    "On-Site Agent offline",
                    $"{message} {lastSeen}{hint}",
                    agent, entry.Slug, ct);
            }
        }

        // Forget agents that were deleted or disabled mid-outage.
        foreach (var staleId in _states.Keys.Where(id => !seen.Contains(id)).ToList())
            _states.Remove(staleId);
        foreach (var staleId in _offlineAlerts.Keys.Where(id => !seen.Contains(id)).ToList())
            _offlineAlerts.Remove(staleId);
        foreach (var staleId in _unstableAlertedAt.Keys.Where(id => !seen.Contains(id)).ToList())
            _unstableAlertedAt.Remove(staleId);
    }

    private Monitoring.OpenAlertSeed OpenAtStart(string siteSlug)
    {
        if (!_openAtStart.TryGetValue(siteSlug, out var seed))
            _openAtStart[siteSlug] = seed = new Monitoring.OpenAlertSeed(_scopes, siteSlug, [OfflineEventType], SeedSubject, _logger);
        return seed;
    }

    /// <summary>Which agent a stored alert is about: the id <see cref="PublishAsync"/> puts in its context.</summary>
    internal static string? SeedSubject(NetworkOptimizer.Alerts.Models.AlertHistoryEntry alert) =>
        Monitoring.OpenAlertSeed.Context(alert, AgentIdKey);

    private const string AgentIdKey = "agent_id";

    private static readonly string ProxyHelp =
        $"See the Reverse proxy section of the agent README: {AgentTunnelRegistry.ReverseProxyHelpUrl} (more help: {AgentTunnelRegistry.AgentConnectionHelpUrl}).";

    /// <summary>Records an offline alert and returns how many came before it within the chronic window.</summary>
    private int RecordOfflineAlert(int agentId, DateTime now)
    {
        if (!_offlineAlerts.TryGetValue(agentId, out var times))
            _offlineAlerts[agentId] = times = new List<DateTime>();
        times.RemoveAll(t => now - t > ChronicWindow);
        var prior = times.Count;
        times.Add(now);
        return prior;
    }

    /// <summary>
    /// The proxy hint for an offline alert, with a leading space, or empty. Added once the outage is
    /// chronic (the Nth alert within a day) or the tunnel flapped on the way down.
    /// </summary>
    internal static string ChronicHint(int alertsInWindow, bool unstable)
    {
        if (alertsInWindow >= ChronicOfflineAlerts)
            return $" It has gone offline {alertsInWindow} times in the last {(int)ChronicWindow.TotalHours} hours. {AgentTunnelRegistry.UnstableCause} {ProxyHelp}";
        return unstable ? $" {AgentTunnelRegistry.UnstableCause} {ProxyHelp}" : "";
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
                SiteSlug = siteSlug == SiteManagementService.DefaultSiteSlug ? null : siteSlug,
                // Pairs an offline with its reconnect across a restart; names can repeat across a site's agents.
                Context = new Dictionary<string, string> { [AgentIdKey] = agent.Id.ToString() }
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

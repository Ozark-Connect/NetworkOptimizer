using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Bridges agent collection to the monitoring pipeline: pushes the site's
/// probe targets and SNMP config to an agent when it connects (and on periodic
/// refresh), and persists what the agent streams back - latency points,
/// interface counters (rates computed here, mirroring the collection agent),
/// and device health, all into the site's own database and Influx buckets.
/// Split out of the tunnel handler so the transport stays free of storage
/// concerns.
/// </summary>
public class AgentProbeResultSink
{
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly MonitoringLiveStatsRegistry _liveStatsRegistry;
    private readonly Monitoring.RebootReason.DeviceRebootRegistry _rebootRegistry;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly Monitoring.DeviceTransitionTracker _deviceTransitions;
    private readonly MonitoringAlertRegistry _alertRegistry;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly Monitoring.IspHealth.IspHealthRegistry _ispHealthRegistry;
    private readonly ClientUsageRollupRegistry _usageRollupRegistry;
    private readonly ILogger<AgentProbeResultSink> _logger;

    // Counter delta cache for agent-relayed interface samples. Key =
    // "slug/deviceMac/ifName" - same rate computation as the local fast tier.
    private readonly ConcurrentDictionary<string, InterfaceRateCalculator.State> _counterCache = new();

    // Device display names per site (slug -> normalized MAC -> name), captured from
    // the device list each SNMP config push assembles. Health samples relayed by the
    // agent carry only the MAC; this gives their alerts a human-readable label.
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _deviceNamesBySite = new();

    /// <summary>
    /// Per-site MAC -> (name, address, firmware) captured whenever the site's console IS
    /// enumerable. The health batch's own console lookup is empty for the first moments after an
    /// agent connects, because the console reconnects THROUGH that same tunnel seconds later - and
    /// reboot detection needs an address to SSH to. Device IPs are stable, so a cached profile
    /// closes that window instead of losing the sample to it.
    /// </summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, DeviceProbeProfile>> _deviceProfilesBySite = new();

    /// <summary>Cached identity for a relayed device: what to call it, where to reach it, what it runs.</summary>
    private sealed record DeviceProbeProfile(string? Name, string? Address, string? Firmware, DeviceType DeviceType);

    // Console device list + networkconf per site, cached so the agent-relayed interface
    // name-map reconcile doesn't hit the controller on every batch. Fetched through the
    // tunneled console.
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<UniFiDeviceResponse> Devices, IReadOnlyList<NetworkInfo> Networks)> _consoleCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _consoleFetchGate = new();
    private static readonly TimeSpan ConsoleCacheTtl = TimeSpan.FromSeconds(60);

    // Agents (site slug + agent id) whose last SNMP push enumeration found zero
    // SNMP-enabled devices. One empty sighting is not proof (a just-reconnected
    // console reports an empty/partial device list for its first moments); only
    // a second consecutive one is adopted as a real disable. Keyed per agent so
    // one agent's transient empty can't fast-track a disable to a sibling agent
    // on the same site. See PushSnmpConfigAsync.
    private readonly ConcurrentDictionary<string, bool> _snmpEmptyEnumerations = new();

    // Samples older than this skip the alert state machines. Agents replay
    // their store-and-forward backlog after a tunnel outage; feeding an
    // hours-old down→up sequence through the evaluators would fire and resolve
    // alerts long after the fact. History still lands in Influx and the live
    // caches (replay is chronological, so the caches end on the newest sample).
    private static readonly TimeSpan AlertFreshness = TimeSpan.FromMinutes(10);

    // Stale samples right after a reconnect are the buffered backlog replaying -
    // expected, and what AlertFreshness exists for. Stale samples on a tunnel
    // that has been up far longer than any backlog takes to drain mean the agent
    // host's clock is behind server time, and every sample is skipping alert
    // evaluation - silently and indefinitely. Warn (rate-limited per site) so
    // the operator knows alerts are not firing and can fix the host's clock/NTP.
    private static readonly TimeSpan ReplayGraceAfterConnect = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SkewWarnInterval = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, DateTime> _skewWarnedAt = new();

    // Per-site topology-boundary aggregator (fabric sums, AP backhaul, gateway WAN),
    // the same LanFabricAggregator the directly-monitored fast tier uses. Keyed by slug
    // because this sink is a singleton serving every agent site.
    private readonly ConcurrentDictionary<string, LanFabricAggregator> _fabricBySite = new();

    // Agent-site SNMP self-heal. The server doesn't poll SNMP for agent sites (the agent
    // does and only relays results), so there's no per-device failure signal to react to
    // like on direct sites. Instead we re-detect the SNMP config from the site's console
    // (reached through the agent's tunnel) on this throttle inside the periodic config
    // push, adopting and re-pushing any change - so a community rotated in the remote
    // UniFi reaches the agent within a couple of minutes instead of never. Keyed by slug.
    private readonly ConcurrentDictionary<string, DateTime> _lastAgentSnmpRedetectAt = new();
    private static readonly TimeSpan AgentSnmpRedetectInterval = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, bool> _fanOidMigratedBySite = new();

    public AgentProbeResultSink(
        SiteDbContextFactory siteDbFactory,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringLiveStatsRegistry liveStatsRegistry,
        Monitoring.RebootReason.DeviceRebootRegistry rebootRegistry,
        SiteConnectionRegistry siteConnections,
        Monitoring.DeviceTransitionTracker deviceTransitions,
        MonitoringAlertRegistry alertRegistry,
        ICredentialProtectionService credentialProtection,
        MonitoringCollectionRegistry collectionRegistry,
        SiteAgentCoverage agentCoverage,
        AgentOnGatewayDetector onGatewayDetector,
        IAgentEnrollmentService enrollment,
        AgentTunnelRegistry tunnelRegistry,
        Monitoring.IspHealth.IspHealthRegistry ispHealthRegistry,
        ClientUsageRollupRegistry usageRollupRegistry,
        ILogger<AgentProbeResultSink> logger)
    {
        _usageRollupRegistry = usageRollupRegistry;
        _ispHealthRegistry = ispHealthRegistry;
        _tunnelRegistry = tunnelRegistry;
        _siteDbFactory = siteDbFactory;
        _influxRegistry = influxRegistry;
        _liveStatsRegistry = liveStatsRegistry;
        _rebootRegistry = rebootRegistry;
        _siteConnections = siteConnections;
        _deviceTransitions = deviceTransitions;
        _alertRegistry = alertRegistry;
        _credentialProtection = credentialProtection;
        _collectionRegistry = collectionRegistry;
        _agentCoverage = agentCoverage;
        _onGatewayDetector = onGatewayDetector;
        _enrollment = enrollment;
        _logger = logger;
    }

    private readonly MonitoringCollectionRegistry _collectionRegistry;
    private readonly SiteAgentCoverage _agentCoverage;
    private readonly AgentOnGatewayDetector _onGatewayDetector;
    private readonly IAgentEnrollmentService _enrollment;
    private readonly AgentTunnelRegistry _tunnelRegistry;

    /// <summary>
    /// Called once per connection after the hello exchange, and again by the periodic refresh.
    /// <paramref name="initialConnect"/> separates the two: it forces the post-connect SNMP re-push,
    /// which a steady-state refresh skips because the config is already current.
    /// </summary>
    public async Task OnAgentConnectedAsync(AgentTunnelConnection connection, CancellationToken ct, bool initialConnect = false)
    {
        if (initialConnect)
            await AdoptHelloFactsAsync(connection);

        await PushProbeConfigAsync(connection, ct);
        await PushSnmpConfigAsync(connection, ct);
        await PushWanSpeedTestConfigAsync(connection, ct);
        await PushConntrackConfigAsync(connection, ct);

        // This site's console reaches the UniFi console THROUGH this agent tunnel.
        // On startup / after an agent restart the console auto-connect can run
        // before the tunnel is up, exhaust its short retry window, and stay
        // disconnected until a manual reconnect. Now that the tunnel is up,
        // reconnect it - fire-and-forget so we never block the tunnel read loop.
        // Never gate this on initialConnect: a tunnel that goes stale and recovers before the 90s
        // watchdog reaps it never reconnects, so the refresh is the only thing left that can clear
        // the awaiting-agent state the stale flip set.
        _ = ReconnectConsoleIfViaAgentAsync(connection, initialConnect);
    }

    /// <summary>
    /// Called from the tunnel teardown. Flips the site's console to the
    /// awaiting-agent state when its last agent drops, so console calls fail
    /// fast instead of retrying against the dead loopback proxy (which stalls
    /// every page of the site for the duration of the retry backoff).
    /// Fire-and-forget: teardown must never block on the console lock.
    /// </summary>
    public void OnAgentDisconnected(AgentTunnelConnection connection)
    {
        // A strike only means "empty enumeration" if the NEXT one is its true
        // consecutive sibling. Across a disconnect the next enumeration comes
        // from a fresh reconnect - exactly the transient-empty condition the
        // two-strike guard exists for - so a held-over strike would fast-track
        // the disable on the first sighting. Start clean.
        _snmpEmptyEnumerations.TryRemove($"{connection.SiteSlug}:{connection.AgentId}", out _);
        _ = Task.Run(async () =>
        {
            try
            {
                await _siteConnections.GetFor(connection.SiteSlug).OnAgentTunnelDroppedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Console awaiting-agent flip failed for site {Slug}", connection.SiteSlug);
            }
        });
    }

    /// <summary>
    /// Called by the tunnel watchdog when a still-registered tunnel goes silent
    /// past the stale threshold (black-holed, not yet droppable at 90s). Flips
    /// the site's console to awaiting-agent proactively, so a site nobody has
    /// touched during the outage doesn't stay stale-green until first contact -
    /// which made the first switch to it pay a dial-and-retry on every console
    /// call. Fire-and-forget; the flip is idempotent.
    /// </summary>
    public void OnTunnelStale(AgentTunnelConnection connection)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _siteConnections.GetFor(connection.SiteSlug).NoteTunnelUnreachableAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Stale-tunnel awaiting-agent flip failed for site {Slug}", connection.SiteSlug);
            }
        });
    }

    private async Task ReconnectConsoleIfViaAgentAsync(AgentTunnelConnection connection, bool initialConnect)
    {
        try
        {
            // The 60s config refresh also lands here while a black-holed tunnel is
            // still registered (the 90s watchdog hasn't reaped it). Reconnecting the
            // console through a tunnel that's silent past the stale threshold just
            // dials the dead loopback proxy and clobbers the awaiting-agent state the
            // proxy's unreachable signal set. Skip; the first refresh after traffic
            // resumes sees a fresh LastMessageAt and proceeds normally.
            if (connection.IsStale)
                return;

            var siteConnection = _siteConnections.GetFor(connection.SiteSlug);
            var reconnected = false;
            if (!siteConnection.IsConnected && await siteConnection.IsConsoleViaAgentAsync())
            {
                _logger.LogInformation(
                    "Agent tunnel up for site {Slug}; reconnecting its console via the tunnel", connection.SiteSlug);
                await siteConnection.ReconnectAsync();
                reconnected = true;
            }

            // A refresh that found the console already up has nothing below to do.
            if (!initialConnect && !reconnected)
                return;

            // The initial SNMP push in OnAgentConnectedAsync was deferred because the console
            // wasn't connected yet (it reaches the console through this same tunnel). Now that it
            // is, re-push so the agent gets the full device list immediately instead of waiting for
            // the next periodic refresh.
            //
            // Reached whether or not the console needed reconnecting here. It used to sit behind an
            // early return that also covered "already connected" - the ordinary case after a server
            // restart, since the console comes up on its own as soon as the tunnel does - so the
            // push was skipped exactly when it was wanted and SNMP lagged probes by a full refresh
            // cycle on every reconnect.
            // ReconnectAsync returns once the console is ROUTED through the tunnel, not once it has
            // authenticated - so IsConnected is still false for a moment afterwards and an
            // immediate push defers for exactly the reason it was retried. Give it a short while to
            // finish coming up; if it takes longer than this, the periodic refresh has it.
            for (var i = 0; i < 10 && !siteConnection.IsConnected; i++)
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);

            if (siteConnection.IsConnected)
            {
                await PushSnmpConfigAsync(connection, CancellationToken.None);

                // Both halves are up now, so anything computed before this point saw a partial
                // site. A report produced between server start and this moment is missing whatever
                // arrives through the console - SNMP above all, which is what classifies load, so
                // an early compute finds no loaded windows and reports a different score for the
                // same day. It is then cached and served until something evicts it, which is why a
                // cold report and a warm one disagreed with nothing in between to reconcile them.
                _ispHealthRegistry.InvalidateSite(connection.SiteSlug);
                _logger.LogDebug(
                    "Agent and console both up for site {Slug}; dropping any ISP Health computed without them",
                    connection.SiteSlug);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Console reconnect on agent connect failed for site {Slug}", connection.SiteSlug);
        }
    }

    /// <summary>
    /// Sends the site's enabled monitoring targets to the agent as a full
    /// replacement set. Also invoked periodically by the tunnel handler so
    /// target edits reach connected agents without a reconnect.
    /// </summary>
    public async Task PushProbeConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        try
        {
            var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);

            // Disabled monitoring stops probing everywhere: every server-side
            // collection tier (latency included) gates on MonitoringSettings.Enabled
            // via ShouldRunNowAsync, so mirror that for agent sites. Push an empty
            // replacement set - the agent stops probing instead of burning cycles
            // on results the sink would only discard. Absent settings mean
            // monitoring was never set up: same treatment, matching the server.
            var monitoringSettings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (monitoringSettings is not { Enabled: true })
            {
                connection.TrySend(new ServerMessage { ProbeConfig = new ProbeConfig() });
                _logger.LogDebug("Monitoring disabled for site {Slug}; pushed empty probe config to agent {Id}",
                    connection.SiteSlug, connection.AgentId);
                return;
            }

            // Retired targets are withheld for the same reason the server does not probe them:
            // their address describes nothing, so an agent probing one only records a false loss.
            var targets = await db.MonitoringTargets
                .AsNoTracking()
                .Where(t => t.Enabled && t.RetiredAt == null)
                .ToListAsync(ct);
            // Before anything reads a context's binding, give one back to any context that lost the
            // chance to have one. Runs here because this is the push that follows an agent's hello,
            // which is exactly when an upgraded agent first reports it can bind.
            if (await HealUnboundGatewayContextsAsync(db, connection, ct))
                _ = await db.SaveChangesAsync(ct);
            var contextsById = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);

            // An agent that owns a WAN context is there to measure that WAN and nothing else: it
            // sits behind a policy-routed source or binds the WAN's own interface, so every probe
            // it runs leaves by that WAN. Handing it the site's ordinary targets as well would
            // measure the secondary WAN and file the result under the primary. Only true once a
            // context names this agent, so a site with no contexts pushes exactly what it always
            // has.
            // Steered means the agent's OWN default route leaves by a WAN that is not the primary -
            // a probe box the gateway policy-routes by MAC, or one running with agent.json's
            // probeSourceIp. It is a vantage behind that WAN and nothing else, so it must not
            // probe anything the primary owns.
            //
            // Two ways an agent is NOT steered even while serving a context. It binds per probe
            // (its context names an interface - a gateway agent), so its own route is untouched.
            // Or its context IS the primary's, which needs no steering to reach: on a failover-only
            // site every unpinned box already leaves by the primary. Both keep the agent eligible
            // as the site's collector, which on a gateway-only site it has to be.
            var primaryWanKey = await ResolvePersistedPrimaryWanKeyAsync(db, ct);
            var agentIsSteeredToWan = contextsById.Values.Any(c =>
                c.AgentId == connection.AgentId
                && string.IsNullOrEmpty(c.InterfaceName)
                && !IsPrimaryWanContext(c, primaryWanKey));

            // Exactly one agent probes the unassigned (primary-WAN) targets. Several agents on a
            // site used to each get the whole set as extra vantage points, which on a site running
            // an agent per WAN means every primary target probed N times for one number. The owner
            // is the lowest-id agent that is CONNECTED and not steered: deterministic, so a refresh
            // does not move the pool around, and self-healing, because the next agent takes it over
            // on the following push if the owner drops. Steered agents are never eligible - their
            // probes leave by the wrong WAN.
            // Only when an agent collects for this site at all. On the main site with collection
            // left to the server, the server probes the unassigned pool itself, and the results of
            // an agent probing it too are discarded on arrival by ShouldRecordResult - so pushing
            // them means the agent runs a set of probes for nothing. The push has to ask the same
            // question the record does, or the two disagree about whose numbers count.
            var agentCoversPrimary = !isDefault || await _agentCoverage.CoversAsync(connection.SiteSlug);
            var unassignedOwnerId = agentCoversPrimary
                ? SelectCollectorAgentId(
                    _tunnelRegistry.GetForSite(connection.SiteSlug).Select(c => c.AgentId),
                    contextsById.Values, primaryWanKey, connection.AgentId)
                : NoCollectorAgentId;

            // An agent running ON the gateway cannot usefully probe it: the target is the box the
            // probe runs on, so every reply is loopback - 0 ms and no loss - which reads as a
            // perfectly healthy gateway precisely when it might not be. Skipped for this agent at
            // push time rather than disabled in the database, so the target stays as the user left
            // it and any other vantage keeps measuring it.
            // The address comes from the detector, which already resolved it while deciding. Asking
            // the enrollment service directly looked equivalent and was not: it is a gated service,
            // this runs on the tunnel's background path with no caller context, and the gate threw -
            // taking the whole push with it, so the site got no targets at all and its monitoring
            // read as total loss.
            // Asked per connection rather than per site: with several agents the site-level verdict
            // correlates against whichever one the registry answers with, so it would skip the
            // gateway target for an agent that is not on the gateway - and miss it for the one that
            // is.
            // The MATCHED address, not the agent's own reported one: the site's target for the
            // gateway carries the address the console knows it by, which is not necessarily the
            // address the agent named itself with.
            // TODO(#1106): this one needs the matched ADDRESS, not a yes/no, so the durable
            // per-agent overload does not drop in - it would have to persist the address too.
            // Same exposure as the others: no console, no match, and the gateway's own target is
            // handed back to the agent that sits on it.
            var selfAddress = await _onGatewayDetector.MatchGatewayAddressAsync(
                connection.SiteSlug, connection.HostAddresses, ct);
            var skippedSelf = 0;

            var config = new ProbeConfig();
            foreach (var target in targets)
            {
                if (!string.IsNullOrEmpty(selfAddress)
                    && string.Equals(target.Address, selfAddress, StringComparison.OrdinalIgnoreCase))
                {
                    skippedSelf++;
                    continue;
                }
                // Context targets are that context's alone: its assigned agent, or no agent when
                // the context is server-probed (the server's own prober binds the source IP).
                // Only UNASSIGNED targets fan out to every ordinary agent as extra vantage
                // points - except to an agent that owns a context, which measures only that. A
                // WanContextId whose row is gone counts as a context with no agent (pushed
                // nowhere) rather than as unassigned - conservative until the row is cleaned up.
                var context = target.WanContextId is int contextId
                    && contextsById.TryGetValue(contextId, out var found) ? found : null;
                if (!ShouldPushTargetToAgent(target.WanContextId != null, context?.AgentId, connection.AgentId,
                        agentIsSteeredToWan, unassignedOwnerId, IsFabricTarget(target.TargetType)))
                    continue;
                config.Targets.Add(new ProbeTargetSpec
                {
                    TargetId = target.TargetId,
                    Address = target.Address,
                    ProbeMode = target.ProbeMode.ToString().ToLowerInvariant(),
                    Port = target.Port ?? 0,
                    PollIntervalSeconds = target.PollIntervalSeconds,
                    PingCount = target.PingCount,
                    TargetType = target.TargetType.ToString().ToLowerInvariant(),
                    // The context's bind rides the target: an interface name for an
                    // on-gateway agent, a source IP for a policy-routed one. The agent
                    // prefers this over its own agent.json default, so one agent can
                    // still serve a context while probing on its own route elsewhere.
                    SourceIp = ResolveSpecSourceIp(context, connection.AgentId),
                });
            }

            connection.TrySend(new ServerMessage { ProbeConfig = config });
            _logger.LogDebug("Pushed {Count} probe target(s) to agent {Id} (site {Slug}){Skipped}",
                config.Targets.Count, connection.AgentId, connection.SiteSlug,
                skippedSelf > 0 ? $" - {skippedSelf} skipped: the agent runs on that target" : "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push probe config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>
    /// The one agent that collects for a site: its SNMP, its fabric targets, and the primary WAN's
    /// targets. The lowest-id CONNECTED agent that is not steered behind a secondary WAN.
    /// <para>
    /// Lowest-id makes it deterministic, so a refresh does not move the workload around; taking it
    /// from the connected set makes it self-healing, because the next agent picks the work up on
    /// the following push if the holder drops. Steered agents are never eligible - everything they
    /// send leaves by the wrong WAN. <paramref name="fallbackAgentId"/> is returned when nothing is
    /// eligible, which keeps a lone steered agent collecting rather than leaving a site dark.
    /// </para>
    /// </summary>
    /// <summary>
    /// Stands in for "no agent collects here", where the server does it. Never a real agent id, so
    /// every ownership comparison simply fails.
    /// </summary>
    internal const int NoCollectorAgentId = -1;

    internal static int SelectCollectorAgentId(
        IEnumerable<int> connectedAgentIds,
        IEnumerable<WanContext> contexts,
        string? primaryWanKey,
        int fallbackAgentId)
    {
        var contextList = contexts as IReadOnlyCollection<WanContext> ?? contexts.ToList();
        return connectedAgentIds
            .Where(id => !contextList.Any(c =>
                c.AgentId == id
                && string.IsNullOrEmpty(c.InterfaceName)
                && !IsPrimaryWanContext(c, primaryWanKey)))
            .DefaultIfEmpty(fallbackAgentId)
            .Min();
    }

    /// <summary>
    /// Which agent currently collects for a site, for display. Same answer the push path acts on,
    /// asked from one place so the page cannot disagree with what is actually happening.
    /// <para>
    /// Null when no agent is connected, and null on a default site whose agent does not cover
    /// collection - there the agent is an ADDITIONAL vantage and this server does the collecting,
    /// so naming an agent would claim a handover that never happened. The push path gates on the
    /// same question before choosing an unassigned owner; this used to skip it and answer with
    /// whichever agent happened to be connected.
    /// </para>
    /// </summary>
    public async Task<int?> GetCollectorAgentIdAsync(string siteSlug, CancellationToken ct = default)
    {
        var connected = _tunnelRegistry.GetForSite(siteSlug).Select(c => c.AgentId).ToList();
        if (connected.Count == 0) return null;
        var isDefault = siteSlug == SiteManagementService.DefaultSiteSlug;
        if (isDefault && !await _agentCoverage.CoversAsync(siteSlug)) return null;
        try
        {
            await using var db = _siteDbFactory.CreateForSite(siteSlug, isDefault);
            var contexts = await db.WanContexts.AsNoTracking().ToListAsync(ct);
            var primaryWanKey = await ResolvePersistedPrimaryWanKeyAsync(db, ct);
            return SelectCollectorAgentId(connected, contexts, primaryWanKey, connected.Min());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the collector agent for site {Slug}", siteSlug);
            return connected.Min();
        }
    }

    /// <summary>
    /// The primary WAN's key as the last connected compute recorded it, or null when none has.
    /// Read from the site's WanProfiles because this path has no console to ask, and a WAN's name
    /// says nothing about its role. Null means unknown: callers must not read it as "not primary".
    /// </summary>
    /// <summary>
    /// Fills the bind interface for this agent's contexts that have none, when the agent runs on the
    /// gateway and can bind.
    /// <para>
    /// The state is reachable without any mistake: save a vantage while the agent is too old to
    /// offer a binding, then update the agent. The capability arrives, the empty configuration does
    /// not change, and the probes go on leaving by the gateway's default route while their results
    /// are filed under the context's WAN - a wrong number that looks exactly like a right one. A
    /// policy-based route cannot rescue it either, because routing policy does not govern the
    /// gateway's OWN egress; binding the interface is the only mechanism there is.
    /// </para>
    /// <para>
    /// Only ever fills an empty binding, so it cannot overwrite a choice. The interface comes from
    /// the WAN's persisted data path - the logical uplink, ppp0 on PPPoE rather than the physical
    /// port - so it needs no console call and works while the console is unreachable.
    /// </para>
    /// </summary>
    /// <returns>Whether anything changed and the caller should save.</returns>
    private async Task<bool> HealUnboundGatewayContextsAsync(
        NetworkOptimizerDbContext db, AgentTunnelConnection connection, CancellationToken ct)
    {
        if (connection.SupportsSourceBind != true) return false;
        var unbound = await db.WanContexts
            .Where(c => c.AgentId == connection.AgentId
                && (c.InterfaceName == null || c.InterfaceName == "")
                && (c.ProbeSourceIp == null || c.ProbeSourceIp == "")
                && c.WanInterface != null && c.WanInterface != "")
            .ToListAsync(ct);
        if (unbound.Count == 0) return false;

        // Asked only when there is something to heal: it can await a console round trip.
        // TODO(#1106): a yes/no, so the durable per-agent overload does drop in here.
        if (await _onGatewayDetector.MatchGatewayAddressAsync(
                connection.SiteSlug, connection.HostAddresses, ct) == null)
            return false;

        var profiles = await db.WanProfiles.AsNoTracking().ToListAsync(ct);
        var healed = false;
        foreach (var context in unbound)
        {
            var key = GatewayWanHelper.WanInterfaceKeyFromKey(context.WanInterface!);
            var dataPath = profiles.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.WanNetworkgroup)
                && string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(p.WanNetworkgroup), key,
                    StringComparison.OrdinalIgnoreCase))?.DataPathInterface;
            if (string.IsNullOrEmpty(dataPath)) continue;
            context.InterfaceName = dataPath;
            healed = true;
            _logger.LogInformation(
                "WAN vantage '{Name}' had no binding; bound it to {Interface} for agent {Id} (site {Slug})",
                context.Name, dataPath, connection.AgentId, connection.SiteSlug);
        }
        return healed;
    }

    private static async Task<string?> ResolvePersistedPrimaryWanKeyAsync(
        NetworkOptimizerDbContext db, CancellationToken ct)
    {
        var group = (await db.WanProfiles.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IsPrimary == true, ct))?.WanNetworkgroup;
        return string.IsNullOrEmpty(group) ? null : GatewayWanHelper.WanInterfaceKeyFromKey(group);
    }

    /// <summary>
    /// Whether a context measures the primary WAN. False when the primary is unknown: an agent is
    /// only excused from being treated as steered on a positive answer, so an unresolved primary
    /// leaves the conservative reading in place rather than handing it the site's targets.
    /// </summary>
    internal static bool IsPrimaryWanContext(WanContext context, string? primaryWanKey) =>
        !string.IsNullOrEmpty(primaryWanKey)
        && !string.IsNullOrEmpty(context.WanInterface)
        && string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(context.WanInterface!),
            primaryWanKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a target belongs in one agent's pushed set.
    ///
    /// Every target has exactly one prober, and which one depends on what the target measures.
    ///
    /// FABRIC targets - the gateway, switches, APs, anything inside the LAN - never cross a WAN, so
    /// no WAN owns them and a context could not mean anything for one. They go to the site's
    /// collector, the same agent that polls SNMP: it is the one inside the network, and pairing the
    /// two keeps a device's counters and its reachability measured from the same place.
    ///
    /// WAN targets belong to the WAN they leave by: a context's targets to that context's agent,
    /// and the unassigned ones - the primary's - to ONE agent rather than all of them, so a site
    /// running an agent per WAN does not probe every primary target once per agent for one number.
    ///
    /// A STEERED agent is probe-only for its context: everything it sends leaves by that WAN, so a
    /// primary target probed from it would measure the wrong path and be recorded as the primary's.
    /// An interface-bound (gateway) agent is not steered - it binds each context probe to that
    /// WAN's interface while its own route stays the primary - so it can serve contexts AND be the
    /// site's collector, which on a gateway-only site it has to be.
    /// </summary>
    /// <param name="targetHasContext">Whether the target belongs to ANY WAN context. A context
    /// target is that context's alone: its assigned agent when it has one, or - for a source-IP
    /// (server-probed) context - NO agent at all, because an ordinary agent would probe it over
    /// its own primary route while the result gets tagged with the secondary WAN's key,
    /// corrupting that WAN's score now that the tag is read.</param>
    /// <param name="contextAgentId">Agent assigned to the target's WAN context; null when the target has no context, or its context has no agent (server-probed).</param>
    /// <param name="agentId">The agent being pushed to.</param>
    /// <param name="agentIsSteeredToWan">Whether a context names this agent WITHOUT an interface
    /// to bind - i.e. the whole box sits behind one WAN.</param>
    /// <param name="unassignedOwnerId">The one agent that collects for the site: fabric targets
    /// and the primary WAN's.</param>
    /// <param name="targetIsFabric">Whether the target is inside the LAN, so no WAN owns it.</param>
    internal static bool ShouldPushTargetToAgent(
        bool targetHasContext, int? contextAgentId, int agentId, bool agentIsSteeredToWan,
        int unassignedOwnerId, bool targetIsFabric = false)
        => targetIsFabric
            ? !agentIsSteeredToWan && unassignedOwnerId == agentId
            : targetHasContext
                ? contextAgentId == agentId
                : !agentIsSteeredToWan && unassignedOwnerId == agentId;

    /// <summary>
    /// Whether a target sits inside the LAN, where no WAN is involved and a WAN context would mean
    /// nothing. Fabric is the type the discovery tier gives the gateway, switches and APs.
    /// </summary>
    internal static bool IsFabricTarget(MonitoringTargetType targetType) =>
        targetType == MonitoringTargetType.Fabric;

    /// <summary>
    /// The source an agent binds this target's probes to: the context's interface when it has one,
    /// otherwise its source IP, and empty for anything the agent is not running on that context's
    /// behalf. Empty leaves the agent on its own configured default, which is what every target
    /// carried before contexts existed.
    /// </summary>
    internal static string ResolveSpecSourceIp(WanContext? context, int agentId)
        => context != null && context.AgentId == agentId
            ? context.InterfaceName ?? context.ProbeSourceIp ?? ""
            : "";

    /// <summary>
    /// Whether a result an agent sent should be written.
    ///
    /// Coverage governs primary-path measurement: a main-site agent that is not covering the site
    /// is a second prober for targets the server is already probing, and its results are dropped so
    /// the two cadences don't saw across the same series. A context's targets are not that - the
    /// server never probes them (it cannot reach the secondary WAN), so the assigned agent's
    /// results are the only ones there are and coverage has no bearing on them.
    /// </summary>
    internal static bool ShouldRecordResult(bool agentCoversPrimary, int? contextAgentId, int agentId)
        => agentCoversPrimary || contextAgentId == agentId;

    /// <summary>
    /// Whether an agent should be sent the site's SNMP config and speed-test server list.
    ///
    /// A STEERED agent is a probe vantage behind one WAN, not a second collector: polling SNMP
    /// from it would double every counter the site already collects, and it serves no speed tests.
    /// An interface-bound (gateway) agent is a collector that also serves contexts, so it keeps
    /// both - a site whose only agent is on the gateway must still get its SNMP from somewhere.
    /// False only once a steered context names it, so a site with no contexts is unaffected.
    /// </summary>
    internal static bool ShouldPushSiteCollectionConfig(bool agentIsSteeredToWan) => !agentIsSteeredToWan;

    /// <summary>
    /// Whether this agent should poll SNMP. Steered agents stand down so the site's collector does
    /// it once, but only when there IS another one to do it: on a site where every agent sits
    /// behind its own WAN, the collector is necessarily a steered agent, and standing it down too
    /// leaves the site with no poller at all. <see cref="SelectCollectorAgentId"/> already falls
    /// back to one for that reason; this is what lets its answer reach the agent.
    /// </summary>
    internal static bool ShouldPushSnmpConfig(bool agentIsSteeredToWan, bool agentIsCollector) =>
        !agentIsSteeredToWan || agentIsCollector;

    /// <summary>
    /// Whether this agent sits ENTIRELY behind one WAN: a context names it and gives no interface
    /// to bind, so the box itself is policy-routed out that WAN. An agent whose contexts all name
    /// an interface binds per probe and still routes normally, so it is not steered. Answers false
    /// when the site database cannot be read, which leaves every gate on this at the behavior it
    /// has today rather than standing an agent down on a hiccup.
    /// </summary>
    private async Task<bool> IsSteeredToWanAgentAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        try
        {
            var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
            var primaryWanKey = await ResolvePersistedPrimaryWanKeyAsync(db, ct);
            var contexts = await db.WanContexts.AsNoTracking()
                .Where(c => c.AgentId == connection.AgentId
                    && (c.InterfaceName == null || c.InterfaceName == ""))
                .ToListAsync(ct);
            return contexts.Any(c => !IsPrimaryWanContext(c, primaryWanKey));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read WAN contexts for agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
            return false;
        }
    }

    /// <summary>
    /// Whether this agent owns any WAN context on its site, however that context binds. The test for
    /// "is there anything worth reading this agent's results for" - the per-result check below then
    /// decides which of them to keep.
    /// </summary>
    private async Task<bool> AgentOwnsAnyContextAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        try
        {
            await using var db = _siteDbFactory.CreateForSite(
                connection.SiteSlug, connection.SiteSlug == SiteManagementService.DefaultSiteSlug);
            return await db.WanContexts.AsNoTracking().AnyAsync(c => c.AgentId == connection.AgentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read WAN contexts for agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
            return false;
        }
    }

    /// <summary>
    /// Re-pushes probe config to every connected agent of a site. Reassigning a WAN context moves
    /// targets between agents, and both ends have to hear about it: the agent losing the context
    /// keeps probing what it no longer owns until it is told otherwise, and the one gaining it does
    /// not start until it is. The periodic refresh would settle both within a minute; this makes
    /// the edit take effect when the user makes it.
    /// </summary>
    public async Task PushProbeConfigToSiteAsync(string siteSlug, CancellationToken ct = default)
    {
        foreach (var connection in _tunnelRegistry.GetForSite(siteSlug))
            await PushProbeConfigAsync(connection, ct);
    }

    /// <summary>
    /// Pushes the WAN speed-test server list (global, main database) so the
    /// agent can serve its /wan/ redirect without the external servers needing
    /// any per-site config: /wan/ goes to the default server, /wan/&lt;id&gt;/ to
    /// that mapped server. Pushed on connect and by the periodic refresh so
    /// Settings edits reach connected agents.
    /// </summary>
    public async Task PushWanSpeedTestConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        // A context-assigned agent serves no speed test page, so it has no /wan/ redirect to
        // resolve and no reason to hold the server list.
        if (!ShouldPushSiteCollectionConfig(await IsSteeredToWanAgentAsync(connection, ct)))
            return;
        try
        {
            await using var db = _siteDbFactory.CreateForSite(SiteManagementService.DefaultSiteSlug, isDefault: true);
            var servers = await db.ExternalSpeedTestServers.AsNoTracking()
                .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Id)
                .ToListAsync(ct);

            var config = new WanSpeedTestConfig();
            foreach (var server in servers)
            {
                if (!server.IsConfigured || string.IsNullOrEmpty(server.ServerId)) continue;
                config.Servers.Add(new WanSpeedTestServer { ServerId = server.ServerId, Url = server.Url });
                if (server.IsDefault && config.DefaultServerId.Length == 0)
                    config.DefaultServerId = server.ServerId;
            }

            connection.TrySend(new ServerMessage { WanSpeedtestConfig = config });
            _logger.LogDebug("Pushed {Count} WAN speed-test server(s) to agent {Id} (site {Slug})",
                config.Servers.Count, connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push WAN speed-test config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>
    /// Builds and pushes the site's SNMP monitoring config: credentials from
    /// the site's MonitoringSettings, device list from the site's console
    /// connection, filtered and addressed by the same SnmpDeviceRules the
    /// local collection agent uses. A default-site agent gets SNMP config only when the site is
    /// configured for its agent to cover it - otherwise the server's own collection agent is still
    /// polling those devices and pushing a second poller would double every sample. A
    /// context-assigned agent gets an explicitly disabled config for the same reason: it is a probe
    /// vantage behind one WAN, and the site already has a collector.
    /// </summary>
    public async Task PushSnmpConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
        if (isDefault && !await _agentCoverage.CoversAsync(connection.SiteSlug)) return;
        var steered = await IsSteeredToWanAgentAsync(connection, ct);
        var isCollector = steered && await GetCollectorAgentIdAsync(connection.SiteSlug, ct) == connection.AgentId;
        if (!ShouldPushSnmpConfig(steered, isCollector))
        {
            // Disabled rather than absent: an agent that polled before being assigned a context
            // keeps polling on its last config until a new one tells it to stop.
            connection.TrySend(new ServerMessage { SnmpConfig = new SnmpConfig { Enabled = false } });
            _logger.LogDebug("Agent {Id} (site {Slug}) probes a WAN context; SNMP polling left to the site's collector",
                connection.AgentId, connection.SiteSlug);
            return;
        }
        try
        {
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);

            // Before building the push, re-detect the SNMP config from the site's console
            // and adopt any change, so a community/credential rotation in the remote UniFi
            // propagates to the agent. Throttled internally; returns the (possibly updated)
            // settings so the config we push below carries the fresh credentials.
            settings = await MaybeRedetectAgentSnmpAsync(connection.SiteSlug, db, settings, ct);

            var config = new SnmpConfig { Enabled = false };
            if (settings is { Enabled: true })
            {
                if (settings.SnmpVersion == SnmpVersionSetting.V2c && !string.IsNullOrEmpty(settings.SnmpCommunity))
                {
                    config.Version = "v2c";
                    config.Community = _credentialProtection.Decrypt(settings.SnmpCommunity);
                    config.Enabled = !string.IsNullOrEmpty(config.Community);
                }
                else if (settings.SnmpVersion == SnmpVersionSetting.V3 && !string.IsNullOrEmpty(settings.SnmpV3Username))
                {
                    config.Version = "v3";
                    config.Username = settings.SnmpV3Username;
                    config.AuthPassword = string.IsNullOrEmpty(settings.SnmpV3AuthPassword)
                        ? ""
                        : _credentialProtection.Decrypt(settings.SnmpV3AuthPassword);
                    config.Enabled = true;
                }
                config.FastIntervalSeconds = Math.Max(2, settings.FastPollIntervalSeconds);
                config.MediumIntervalSeconds = Math.Max(10, settings.MediumPollIntervalSeconds);
            }

            if (config.Enabled)
            {
                var siteConnection = _siteConnections.GetFor(connection.SiteSlug);
                if (!siteConnection.IsConnected || siteConnection.Client == null)
                {
                    // SNMP is enabled in settings, but the site's console isn't connected
                    // yet - on an agent-routed site it reconnects THROUGH this same tunnel
                    // moments after this callback. We can't enumerate devices now, so skip
                    // this push rather than sending Enabled=false, which would stop the
                    // agent's SNMP polling. ReconnectConsoleIfViaAgentAsync re-pushes the
                    // full config once the console is up.
                    _logger.LogDebug(
                        "SNMP enabled for site {Slug} but its console isn't connected yet; deferring SNMP config push",
                        connection.SiteSlug);
                    return;
                }

                var devices = await siteConnection.Client.GetDevicesAsync(ct) ?? new();
                string? gatewayLanIp = null;
                try { gatewayLanIp = await Monitoring.SnmpDeviceRules.ResolveGatewayLanIpAsync(siteConnection.Client, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "Gateway LAN IP resolution failed for site {Slug}", connection.SiteSlug); }

                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Every device, not just the SNMP-polled ones: the reboot probe reaches anything
                // with SSH, and this is the only place the site's console is known to be readable.
                // Fenced because this cache only feeds reboot-reason lookups - the SNMP config push
                // below is the load-bearing work here and must not be affected by it.
                try
                {
                    var profiles = new Dictionary<string, DeviceProbeProfile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var device in devices)
                    {
                        if (string.IsNullOrEmpty(device.Mac)) continue;
                        profiles[NormalizeMac(device.Mac)] = new DeviceProbeProfile(
                            device.Name,
                            Monitoring.SnmpDeviceRules.ResolvePollAddress(device, gatewayLanIp),
                            device.Version,
                            device.DeviceType);
                    }
                    _deviceProfilesBySite[connection.SiteSlug] = profiles;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Device profile cache build failed for site {Slug}", connection.SiteSlug);
                }

                foreach (var device in devices.Where(d =>
                             Monitoring.SnmpDeviceRules.IsMonitorable(d) && Monitoring.SnmpDeviceRules.HasSnmpEnabled(d)))
                {
                    config.Devices.Add(new SnmpDeviceSpec
                    {
                        Mac = device.Mac,
                        Ip = Monitoring.SnmpDeviceRules.ResolvePollAddress(device, gatewayLanIp),
                        Name = device.Name ?? "",
                        // The agent echoes this string straight back into the device_health tag, so
                        // it has to be the same label the server's own writes use. ToString() is not:
                        // it spells an AP "accesspoint" where the server writes "ap", which forks the
                        // device into a second series the moment collection changes hands.
                        DeviceType = MonitoringCollectionAgent.DescribeDeviceType(device.DeviceType),
                    });
                    if (!string.IsNullOrEmpty(device.Name))
                        names[NormalizeMac(device.Mac)] = device.Name;
                }
                _deviceNamesBySite[connection.SiteSlug] = names;

                // Custom OIDs: push the site's enabled per-device OIDs for the devices the
                // agent is polling, mirroring the directly-monitored medium tier's custom-OID
                // collection. The agent gets/walks them; the server parses and stores the raw
                // values it relays back.
                if (config.Devices.Count > 0)
                {
                    if (!_fanOidMigratedBySite.ContainsKey(connection.SiteSlug))
                    {
                        await CustomOidMigration.RemoveSupersededAsync(db, connection.SiteSlug, _logger, ct);
                        _fanOidMigratedBySite[connection.SiteSlug] = true;
                    }

                    var configuredMacs = new HashSet<string>(
                        config.Devices.Select(d => NormalizeMac(d.Mac)), StringComparer.OrdinalIgnoreCase);
                    var customOids = await db.CustomOidConfigurations.AsNoTracking()
                        .Where(c => c.Enabled)
                        .ToListAsync(ct);
                    foreach (var oid in customOids)
                    {
                        if (!configuredMacs.Contains(NormalizeMac(oid.DeviceMac))) continue;
                        config.CustomOids.Add(new SnmpCustomOid
                        {
                            DeviceMac = oid.DeviceMac,
                            Oid = oid.Oid,
                            FieldName = oid.FieldName,
                            ValueType = (int)oid.ValueType,
                            Scope = (int)oid.Scope,
                        });
                    }
                }

                if (config.Devices.Count == 0)
                {
                    // Console is up but enumerated no SNMP-enabled devices. One
                    // sighting is not proof the site really has none: a console
                    // that just reconnected (server restart, tunnel bounce)
                    // reports an empty/partial device list for its first
                    // moments, and disabling on that stopped the agent's SNMP
                    // polling until the next refresh cycle - a ~60 s data gap
                    // per server restart, seen live on an agent site 2026-07-30.
                    // Skip the push (the agent keeps its last-known-good config)
                    // and adopt the disable only when a second consecutive
                    // enumeration agrees.
                    if (_snmpEmptyEnumerations.TryAdd($"{connection.SiteSlug}:{connection.AgentId}", true))
                    {
                        _logger.LogDebug(
                            "SNMP push for site {Slug}: console enumerated no SNMP-enabled devices; keeping agent {Id}'s last config until a second enumeration confirms",
                            connection.SiteSlug, connection.AgentId);
                        return;
                    }
                    config.Enabled = false;
                }
                else
                {
                    _snmpEmptyEnumerations.TryRemove($"{connection.SiteSlug}:{connection.AgentId}", out _);
                }
            }

            connection.TrySend(new ServerMessage { SnmpConfig = config });
            if (config.Enabled)
                _logger.LogDebug("Pushed SNMP config with {Count} device(s) to agent {Id} (site {Slug})",
                    config.Devices.Count, connection.AgentId, connection.SiteSlug);
            else
                _logger.LogDebug("Pushed disabled SNMP config to agent {Id} (site {Slug})",
                    connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push SNMP config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>
    /// Re-detects the SNMP config from an agent site's console (reached through the
    /// agent's tunnel) and adopts it if it changed, on a per-site throttle. This is the
    /// agent-site equivalent of the direct-poll self-heal: because the server never sees
    /// the agent's per-device SNMP failures, it can't react to them, so instead it
    /// proactively re-pulls the config every couple of minutes and lets the caller push
    /// any change. Returns the (possibly updated) settings so the push carries the fresh
    /// credentials. A too-long community is left alone (re-pulling returns the same value
    /// the devices already reject; the Setup tab surfaces the length warning).
    /// </summary>
    private async Task<MonitoringSettings?> MaybeRedetectAgentSnmpAsync(
        string siteSlug, NetworkOptimizerDbContext db, MonitoringSettings? settings, CancellationToken ct)
    {
        if (settings is not { Enabled: true }) return settings;
        // Disabled stays in the rotation: if SNMP was turned off in the remote UniFi and we
        // adopted that, we must keep re-detecting to notice it being turned back ON (the
        // ConfigDiffers Disabled->Enabled transition). Only NotChecked - detection never
        // ran for this site - is excluded.
        if (settings.SnmpDetectionState == SnmpDetectionState.NotChecked)
            return settings;

        if (_lastAgentSnmpRedetectAt.TryGetValue(siteSlug, out var last)
            && DateTime.UtcNow - last < AgentSnmpRedetectInterval)
            return settings;

        var siteConnection = _siteConnections.GetFor(siteSlug);
        if (!siteConnection.IsConnected || siteConnection.Client == null)
            return settings; // console (via tunnel) not up yet; retry on the next push

        _lastAgentSnmpRedetectAt[siteSlug] = DateTime.UtcNow;

        try
        {
            SnmpDetectionResult detected;
            using (var raw = await siteConnection.Client.GetSettingsRawAsync(ct))
            {
                if (raw == null) return settings;
                detected = SnmpDetectionService.ParseSnmpSettings(raw);
            }
            if (!detected.Success) return settings;

            // Mirror the sighting onto the site's collection agent instance - the site's
            // Monitoring page reads that flag (scoped DI resolves per-site), so a managed
            // site's banner auto-appears and auto-dismisses just like the default site's.
            _collectionRegistry.GetFor(siteSlug).NoteExternalDetection(detected.CommunityTooLong);

            if (detected.CommunityTooLong)
            {
                _logger.LogWarning(
                    "SNMP self-heal (agent site {Slug}): UniFi Community String is {Len} chars, over the reliable {Max}-char device max. Not adopting - it must be shortened.",
                    siteSlug, detected.Community?.Length, SnmpDetectionResult.MaxSupportedCommunityLength);
                return settings;
            }

            if (!SnmpDetectionService.ConfigDiffers(settings, detected, _credentialProtection))
                return settings; // unchanged - nothing to adopt

            var row = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (row == null) return settings;
            var before = row.SnmpDetectionState;
            SnmpDetectionService.ApplyToSettings(row, detected, _credentialProtection);
            row.LastSnmpDetection = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogWarning(
                "SNMP self-heal (agent site {Slug}): adopted updated SNMP config from UniFi ({Before} -> {After}); re-pushing to agent.",
                siteSlug, before, row.SnmpDetectionState);
            return row;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SNMP self-heal re-detect failed for agent site {Slug}", siteSlug);
            return settings;
        }
    }

    /// <summary>
    /// Cached (~60s) console device list + networkconf for a site. Returns whatever is
    /// cached immediately and NEVER performs a live fetch on the caller's thread. It is
    /// called inline on the tunnel read loop, and a console fetch travels back over that
    /// same tunnel - awaiting it here would block the read loop from processing its own
    /// ProxyOpenResult, self-deadlocking for the full open timeout and starving every
    /// other proxied connection (console page loads, SSH, tc-monitor). A stale or missing
    /// cache kicks a background refresh that a later batch reads.
    /// </summary>
    private (IReadOnlyList<UniFiDeviceResponse> Devices, IReadOnlyList<NetworkInfo> Networks) GetConsoleData(string slug)
    {
        var hasCache = _consoleCache.TryGetValue(slug, out var cached);
        if (!hasCache || DateTime.UtcNow - cached.At >= ConsoleCacheTtl)
            _ = RefreshConsoleCacheAsync(slug);
        return hasCache ? (cached.Devices, cached.Networks)
                        : (Array.Empty<UniFiDeviceResponse>(), Array.Empty<NetworkInfo>());
    }

    /// <summary>
    /// Refreshes the console cache for a site off the tunnel read loop (fired from
    /// <see cref="GetConsoleData"/>). Single-flight per site via a non-blocking gate, and
    /// advances the cache timestamp on every outcome - success, console-down, or failure -
    /// so a degraded console backs off a full TTL instead of being re-hammered on every
    /// SNMP batch.
    /// </summary>
    private async Task RefreshConsoleCacheAsync(string slug)
    {
        var gate = _consoleFetchGate.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) return; // a refresh is already running for this site
        try
        {
            var hadCache = _consoleCache.TryGetValue(slug, out var cached);
            if (hadCache && DateTime.UtcNow - cached.At < ConsoleCacheTtl)
                return; // another refresh just landed

            IReadOnlyList<UniFiDeviceResponse> devices = hadCache ? cached.Devices : Array.Empty<UniFiDeviceResponse>();
            IReadOnlyList<NetworkInfo> networks = hadCache ? cached.Networks : Array.Empty<NetworkInfo>();

            var conn = _siteConnections.GetFor(slug);
            if (conn.IsConnected && conn.Client != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    devices = await conn.Client.GetDevicesAsync(cts.Token) ?? devices;
                    try { networks = await conn.GetNetworksAsync(cts.Token); } catch { /* keep prior labels */ }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Console fetch for interface name map failed for site {Slug}", slug);
                }
            }

            // Advance the timestamp regardless of outcome so a down/failing console backs
            // off a full TTL rather than retrying every batch.
            _consoleCache[slug] = (DateTime.UtcNow, devices, networks);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Records SNMP samples relayed by an agent: interface counters go through
    /// the same InterfaceRateCalculator as the local fast tier (32-bit wrap,
    /// reset confirmation, implausible-rate rejection) and land in the site's
    /// buckets; health samples map straight to the device_health measurement.
    /// </summary>
    /// <summary>
    /// Why the agent's SNMP is not reading this device, for the port_table fallback: never heard,
    /// or not within the hand-over gap. Null while it is being read.
    /// </summary>
    private static string? WhySnmpUnheard(MonitoringLiveStats liveStats, UniFiDeviceResponse device, DateTime now)
    {
        var seen = liveStats.GetSnmpLastSeen(device.Mac);
        if (seen == null) return "the agent has never streamed SNMP for it";
        var gap = now - seen.Value;
        return gap < PortTableCounterRecorder.HandoverGap ? null : $"the agent last streamed SNMP for it {gap.TotalMinutes:F0} min ago";
    }

    public async Task RecordSnmpBatchAsync(AgentTunnelConnection connection, SnmpResultBatch batch, CancellationToken ct)
    {
        if (batch.Interfaces.Count == 0 && batch.Health.Count == 0) return;

        var influx = _influxRegistry.GetFor(connection.SiteSlug);
        if (!influx.IsConfigured) await influx.ReconfigureAsync(ct);
        var liveStats = _liveStatsRegistry.GetFor(connection.SiteSlug);
        var rebootTracker = _rebootRegistry.GetFor(connection.SiteSlug);
        await rebootTracker.SeedFromHistoryAsync(ct);

        // Temperature thresholds for health alerting come from the site's own
        // MonitoringSettings, same as the local medium tier.
        MonitoringSettings? settings = null;
        if (batch.Health.Count > 0)
        {
            try
            {
                var isDefaultSite = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
                await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefaultSite);
                settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load MonitoringSettings for site {Slug} health alerting", connection.SiteSlug);
            }
        }

        // Console device list (cached, fetched off the tunnel read loop): drives the
        // gateway live-port-state resilience in the loop below, the topology aggregates
        // after it, and the name-map reconcile - all of which read the site's UniFi
        // port_table (the server can't SNMP-walk a remote agent site).
        var console = batch.Interfaces.Count > 0 || batch.Health.Count > 0
            ? GetConsoleData(connection.SiteSlug)
            : (Devices: (IReadOnlyList<UniFiDeviceResponse>)Array.Empty<UniFiDeviceResponse>(),
               Networks: (IReadOnlyList<NetworkInfo>)Array.Empty<NetworkInfo>());
        var deviceByMac = console.Devices
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .GroupBy(d => NormalizeMac(d.Mac))
            .ToDictionary(g => g.Key, g => g.First());

        // Upgrade/provisioning state for this site's devices, from the same console snapshot the
        // batch already uses, so an agent site's offline alerts stay quiet during a firmware run
        // exactly like a directly-monitored site's do.
        // Fenced as a whole: alerting is advisory here, and a bus failure must not cost this batch
        // its interface and health persistence.
        var stateObservedAt = DateTime.UtcNow;
        try
        {
            foreach (var device in deviceByMac.Values)
            {
                _deviceTransitions.Record(connection.SiteSlug, device.Mac, device.State, stateObservedAt);
                await _alertRegistry.GetFor(connection.SiteSlug).DeviceState.EvaluateAsync(
                    device.Mac, device.Name, device.Ip, device.DeviceType, device.State, stateObservedAt, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cancellation still unwinds: the tunnel shutting down is not an alerting failure.
            _logger.LogDebug(ex, "Device state alert evaluation failed for site {Slug}", connection.SiteSlug);
        }

        // Topology-boundary aggregates (fabric sums, AP backhaul, gateway WAN), shared
        // verbatim with the directly-monitored fast tier via LanFabricAggregator so
        // secondary sites compute identical numbers. Feed the UniFi port_table + device
        // byte deltas first (fallback rates); the SNMP per-interface rates in the loop
        // override them, mirroring the main tier's ordering.
        var fabric = _fabricBySite.GetOrAdd(connection.SiteSlug, _ => new LanFabricAggregator());
        var aggNow = DateTime.UtcNow;
        if (deviceByMac.Count > 0)
            fabric.UpdateUnifiPortRates(console.Devices, aggNow);
        // Per-device fabric-sum + mesh-AP (vwiresta) accumulators, mirroring the fast tier.
        var fabricSum = new Dictionary<string, (double In, double Out)>();
        var meshUplink = new Dictionary<string, (double In, double Out)>();

        foreach (var sample in batch.Interfaces)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(sample.TimestampUnixMs).UtcDateTime;
            // Stamp last-seen so the SNMP Devices status table shows agent-polled devices
            // as Polling (the server's own SNMP tracker is empty on agent-covered sites).
            liveStats.RecordSnmpSeen(sample.DeviceMac, timestamp);
            var key = $"{connection.SiteSlug}/{sample.DeviceMac}/{sample.IfName}";
            InterfaceRateCalculator.State? prevState =
                _counterCache.TryGetValue(key, out var cached) ? cached : null;
            var calc = InterfaceRateCalculator.Compute(
                prevState, sample.InOctets, sample.OutOctets, timestamp, sample.HcCounters, sample.SpeedBps);
            _counterCache[key] = calc.NewState;

            // Mirror into the site's live caches the same way the local fast
            // tier does, so the site's Live View port table and map refresh
            // from memory.
            if (calc.RateInBps.HasValue && calc.RateOutBps.HasValue)
                liveStats.RecordPortRate(sample.DeviceMac, sample.IfName, calc.RateOutBps.Value, calc.RateInBps.Value, timestamp, sample.PortId);

            // Feed the shared fabric aggregator, mirroring the fast tier: SNMP
            // per-interface rate -> port_table PortIdx (the primary port rate), fabric-sum
            // accumulation (physical ports only), and mesh-AP (vwiresta) backhaul.
            if (calc.RateInBps.HasValue && calc.RateOutBps.HasValue
                && deviceByMac.TryGetValue(NormalizeMac(sample.DeviceMac), out var aggDevice))
            {
                var pIdx = InterfacePortCorrelation
                    .Correlate(aggDevice.PortTable, sample.IfIndex, sample.SpeedBps, sample.PortId, sample.IfName)
                    .PortNumber ?? 0;
                if (pIdx > 0)
                    fabric.SetSnmpPortRate(NormalizeMac(sample.DeviceMac), pIdx, calc.RateInBps.Value, calc.RateOutBps.Value);

                var dType = aggDevice.DeviceType;
                if ((dType == DeviceType.Switch || dType == DeviceType.Gateway || dType == DeviceType.CellularModem)
                    && LanFabricAggregator.IncludeInFabricSum(dType, sample.IfDescr))
                {
                    var fk = NormalizeMac(sample.DeviceMac);
                    var cur = fabricSum.TryGetValue(fk, out var f) ? f : (0.0, 0.0);
                    fabricSum[fk] = (cur.Item1 + calc.RateInBps.Value, cur.Item2 + calc.RateOutBps.Value);
                }
                else if (dType == DeviceType.AccessPoint
                         && !string.IsNullOrEmpty(sample.IfDescr)
                         && sample.IfDescr.StartsWith("vwiresta", StringComparison.OrdinalIgnoreCase)
                         && !sample.IfDescr.Contains('.'))
                {
                    // Summed, not assigned: an MLO backhaul has one vwiresta slave per link,
                    // mirroring the fast tier's accumulation.
                    var meshKey = NormalizeMac(sample.DeviceMac);
                    var meshCur = meshUplink.TryGetValue(meshKey, out var mPrev) ? mPrev : (0.0, 0.0);
                    meshUplink[meshKey] = (meshCur.Item1 + calc.RateInBps.Value, meshCur.Item2 + calc.RateOutBps.Value);
                }
            }

            // Live port-state resilience for gateways: when UniFi's port_table says the
            // port is down or disabled and no frame counters moved this poll, mark the live
            // port down - SNMP on a gateway keeps a dead port reporting a stale "up" at a
            // placeholder 10 Mbps. Live cache only; the InfluxDB write below keeps the raw
            // SNMP ifOperStatus.
            // KEEP IN SYNC with the directly-monitored fast tier in
            // MonitoringCollectionAgent.RecordInterfaceSample ("Live port-state resilience
            // for gateways") - if you adjust one, adjust the other.
            int liveOperStatus = sample.OperStatus;
            if (deviceByMac.TryGetValue(NormalizeMac(sample.DeviceMac), out var portDevice)
                && portDevice.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Gateway
                && !(calc.RateInBps > 0) && !(calc.RateOutBps > 0))
            {
                var uniPort = portDevice.PortTable?.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.IfName)
                    && string.Equals(p.IfName, sample.IfName, StringComparison.OrdinalIgnoreCase));
                if (uniPort != null && (!uniPort.Up || !uniPort.Enable))
                    liveOperStatus = 2; // ifOperStatus down
            }

            liveStats.RecordPortStats(new MonitoringInfluxClient.PortStatsPoint
            {
                DeviceMac = sample.DeviceMac,
                IfName = sample.IfName,
                PortId = sample.PortId,
                OperStatus = liveOperStatus,
                SpeedBps = sample.SpeedBps > 0 ? sample.SpeedBps : null,
                RateInBps = calc.RateInBps,
                RateOutBps = calc.RateOutBps,
                BytesIn = sample.InOctets,
                BytesOut = sample.OutOctets,
                UcastPktsIn = sample.UcastPktsIn,
                UcastPktsOut = sample.UcastPktsOut,
                McastPktsIn = sample.McastPktsIn,
                McastPktsOut = sample.McastPktsOut,
                BcastPktsIn = sample.BcastPktsIn,
                BcastPktsOut = sample.BcastPktsOut,
                ErrorsIn = sample.ErrorsIn,
                ErrorsOut = sample.ErrorsOut,
                DiscardsIn = sample.DiscardsIn,
                DiscardsOut = sample.DiscardsOut,
                Time = timestamp,
            });

            // A read the calculator does not trust is not stored: differenced, one bad sample
            // reads as the whole counter's worth of traffic.
            if (calc.Outcome is InterfaceRateCalculator.Outcome.ResetPending or InterfaceRateCalculator.Outcome.ImplausibleRate)
                continue;

            await influx.WriteInterfaceCountersAsync(
                deviceMac: sample.DeviceMac,
                ifName: sample.IfName,
                portId: sample.PortId,
                direction: InterfaceDirection.Unknown,
                bytesIn: sample.InOctets,
                bytesOut: sample.OutOctets,
                rateInBps: calc.RateInBps,
                rateOutBps: calc.RateOutBps,
                speedBps: sample.SpeedBps > 0 ? sample.SpeedBps : null,
                operStatus: sample.OperStatus,
                errorsIn: sample.ErrorsIn,
                errorsOut: sample.ErrorsOut,
                discardsIn: sample.DiscardsIn,
                discardsOut: sample.DiscardsOut,
                hcCounters: sample.HcCounters,
                ucastPktsIn: sample.UcastPktsIn > 0 ? sample.UcastPktsIn : null,
                ucastPktsOut: sample.UcastPktsOut > 0 ? sample.UcastPktsOut : null,
                mcastPktsIn: sample.McastPktsIn > 0 ? sample.McastPktsIn : null,
                mcastPktsOut: sample.McastPktsOut > 0 ? sample.McastPktsOut : null,
                bcastPktsIn: sample.BcastPktsIn > 0 ? sample.BcastPktsIn : null,
                bcastPktsOut: sample.BcastPktsOut > 0 ? sample.BcastPktsOut : null,
                timestamp: timestamp);
        }

        // Publish fabric sums + mesh-AP backhaul, then the topology-boundary aggregates -
        // mirroring the fast tier's post-loop passes (vwiresta + fabric recorded BEFORE
        // WriteAggregates so its mesh pass sees them via GetForDevice). Uses each console
        // device's real MAC so live-stats keys line up with the directly-monitored path.
        if (deviceByMac.Count > 0)
        {
            foreach (var (fk, sum) in fabricSum)
                if (deviceByMac.TryGetValue(fk, out var fsDev))
                    liveStats.RecordFabricSum(fsDev.Mac, sum.In, sum.Out, aggNow);
            foreach (var (mk, m) in meshUplink)
                if (deviceByMac.TryGetValue(mk, out var mDev))
                    // vwiresta rateIn = downloads, rateOut = uploads; swap to match the
                    // fast tier's RecordInterfaceAggregate(mac, out, in) convention.
                    liveStats.RecordInterfaceAggregate(mDev.Mac, m.Out, m.In, aggNow);
            fabric.WriteAggregates(console.Devices, liveStats, aggNow);
            // Persist UDB downlink port_table rates to interface_counters for the historic
            // resolver - identical to the directly-monitored fast tier (in-memory-only live path,
            // and a UDB has no SNMP interface series to re-derive from during playback).
            BridgeInterfaceRecorder.Record(fabric, console.Devices, influx, aggNow);
            // Mesh backhaul PHY, identical to the fast tier, so agent-relayed sites scrub the
            // maps' Link speed the same as directly-monitored ones.
            MeshBackhaulPhyRecorder.Record(console.Devices, influx, aggNow);
            // A switch the agent's SNMP has not read within the hand-over gap has its port_table
            // counters recorded instead, as the directly-monitored fast tier does for the switches
            // it skips. The agent never streams a switch that does not answer, so unheard is the
            // signal here; the gap keeps a momentary miss from switching sources.
            PortTableCounterRecorder.Record(console.Devices, d => WhySnmpUnheard(liveStats, d, aggNow),
                _counterCache, $"{connection.SiteSlug}/", influx, liveStats, _logger, aggNow);
        }

        // Reconcile the InterfaceNameMap (friendly name, negotiated speed, port number,
        // SFP) + interface labels for this site the way the directly-monitored slow tier
        // does - but the server can't SNMP-walk an agent site, so the interface
        // enumeration comes from the agent's streamed samples and the gap-fill from the
        // site's UniFi port table + networkconf (via the tunneled console, cached). The
        // shared InterfacePortCorrelation helper is the exact per-interface logic the main
        // path runs; the main path itself is untouched.
        if (batch.Interfaces.Count > 0)
        {
            try
            {
                if (console.Devices.Count > 0)
                {
                    // console + deviceByMac are the cached snapshot captured above for the
                    // resilience + aggregates; reuse them rather than re-fetch/rebuild.
                    var isDefaultSite = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
                    await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefaultSite);
                    var existingMaps = await db.InterfaceNameMaps.ToDictionaryAsync(
                        m => (m.DeviceMac, m.IfName), m => m, ct);

                    foreach (var deviceGroup in batch.Interfaces.GroupBy(s => NormalizeMac(s.DeviceMac)))
                    {
                        if (!deviceByMac.TryGetValue(deviceGroup.Key, out var device)) continue;
                        var ifNames = new List<string>();
                        foreach (var sample in deviceGroup)
                        {
                            if (string.IsNullOrEmpty(sample.IfName)) continue;
                            ifNames.Add(sample.IfName);
                            var corr = InterfacePortCorrelation.Correlate(
                                device.PortTable, sample.IfIndex, sample.SpeedBps, sample.PortId, sample.IfName);
                            var mapKey = (deviceGroup.Key, sample.IfName);
                            if (!existingMaps.TryGetValue(mapKey, out var mapping))
                            {
                                db.InterfaceNameMaps.Add(existingMaps[mapKey] = new InterfaceNameMap
                                {
                                    DeviceMac = deviceGroup.Key,
                                    IfName = sample.IfName,
                                    FriendlyName = corr.FriendlyName,
                                    PortNumber = corr.PortNumber,
                                    SpeedMbps = corr.LinkSpeedMbps,
                                    IsSfp = corr.IsSfp,
                                    LastUpdated = DateTime.UtcNow
                                });
                            }
                            else
                            {
                                if (corr.FriendlyName != null) mapping.FriendlyName = corr.FriendlyName;
                                if (corr.PortNumber.HasValue)
                                {
                                    mapping.PortNumber = corr.PortNumber;
                                }
                                else if (mapping.PortNumber is int stale
                                    && InterfacePortCorrelation.PortNumberBelongsToOtherInterface(device.PortTable, sample.IfName, stale, sample.PortId))
                                {
                                    // Heal rows written before the numeric ifIndex match was
                                    // gated to entries without an ifname: the stored number
                                    // (and the friendly name / SFP flag copied with it)
                                    // belongs to the interface the port_table entry names.
                                    mapping.PortNumber = null;
                                    mapping.FriendlyName = null;
                                    mapping.IsSfp = null;
                                }
                                if (corr.LinkSpeedMbps.HasValue) mapping.SpeedMbps = corr.LinkSpeedMbps;
                                if (corr.IsSfp.HasValue) mapping.IsSfp = corr.IsSfp;
                                mapping.LastUpdated = DateTime.UtcNow;
                            }
                        }
                        // Heal rows for interfaces the agent no longer streams - same
                        // sweep as the directly-monitored slow tier: a stored port
                        // number that provably belongs to another interface is a relic
                        // of the ungated numeric ifIndex match and would otherwise
                        // stick forever once its interface leaves the sample set.
                        // No rawIfName here BY DESIGN: these rows have no current
                        // sample to supply one, so an alias-keyed row whose interface
                        // has left the stream can be cleared - accepted, since a
                        // departed interface's port claim is stale anyway.
                        var streamedNames = new HashSet<string>(ifNames, StringComparer.OrdinalIgnoreCase);
                        foreach (var ((rowMac, rowIfName), row) in existingMaps)
                        {
                            if (rowMac != deviceGroup.Key || streamedNames.Contains(rowIfName)) continue;
                            if (row.PortNumber is int staleClaim
                                && InterfacePortCorrelation.PortNumberBelongsToOtherInterface(device.PortTable, rowIfName, staleClaim))
                            {
                                row.PortNumber = null;
                                row.FriendlyName = null;
                                row.IsSfp = null;
                                row.LastUpdated = DateTime.UtcNow;
                            }
                        }

                        liveStats.RecordInterfaceLabels(deviceGroup.Key,
                            InterfaceLabelResolver.BuildLabels(device, console.Networks, ifNames));
                    }
                    // Name-map rows for the switches the agent is not reading, from their port
                    // tables - the same switches the port_table recorder above writes.
                    var heardAt = DateTime.UtcNow;
                    foreach (var device in PortTableCounterRecorder.Uncovered(console.Devices, d => WhySnmpUnheard(liveStats, d, heardAt)))
                        PortTableCounterRecorder.ReconcileNameMaps(device, existingMaps, db);
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Interface name map reconcile from agent samples failed for site {Slug}", connection.SiteSlug);
            }
        }

        foreach (var health in batch.Health)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(health.TimestampUnixMs).UtcDateTime;
            liveStats.RecordSnmpSeen(health.DeviceMac, timestamp);

            double? cpu = health.HasCpuPercent ? health.CpuPercent : null;
            double? mem = health.HasMemoryUsedPercent ? health.MemoryUsedPercent : null;
            double? temp = health.HasTemperatureC ? health.TemperatureC : null;
            long? uptime = health.HasUptimeSeconds ? health.UptimeSeconds : null;
            int? fanRpm = health.HasFanSpeedRpm ? health.FanSpeedRpm : null;

            // Fill health fields SNMP didn't return from the console's cached UniFi device
            // data, mirroring the directly-monitored medium tier's CollectApiHealthFallbackAsync:
            // when SNMP reported cpu/mem, only supplement temperature (and only on switches and
            // gateways); when SNMP reported no health at all, fill whatever the API has.
            if (deviceByMac.TryGetValue(NormalizeMac(health.DeviceMac), out var apiDevice))
            {
                var api = UniFiDeviceHealthReader.ExtractApiHealth(apiDevice);
                var snmpHasHealth = health.HasCpuPercent || health.HasMemoryUsedPercent;
                if (snmpHasHealth)
                {
                    var isSwitchOrGateway = apiDevice.DeviceType == DeviceType.Switch
                        || apiDevice.DeviceType == DeviceType.Gateway;
                    if (isSwitchOrGateway && temp == null) temp = api.TemperatureC;
                }
                else
                {
                    cpu ??= api.Cpu;
                    mem ??= api.MemPercent;
                    temp ??= api.TemperatureC;
                    uptime ??= api.UptimeSeconds;
                }
            }

            await influx.WriteDeviceHealthAsync(
                deviceMac: health.DeviceMac,
                deviceType: string.IsNullOrEmpty(health.DeviceType) ? "unknown" : health.DeviceType,
                cpuPercent: cpu,
                memoryTotalKb: health.HasMemoryTotalKb ? health.MemoryTotalKb : null,
                memoryUsedKb: health.HasMemoryUsedKb ? health.MemoryUsedKb : null,
                memoryUsedPercent: mem,
                temperatureC: temp,
                uptimeSeconds: uptime,
                timestamp: timestamp,
                fanSpeedRpm: fanRpm);

            liveStats.RecordHealth(
                health.DeviceMac,
                cpu,
                mem,
                temp,
                uptime,
                timestamp);

            // Agent-relayed sites get their reboot reasons the same way direct ones do. This is
            // the only uptime feed for them: the server's own medium tier stands down while an
            // agent covers collection, so without this the whole site shows no reasons at all.
            // The probe's SSH reaches the site's devices back through the agent tunnel.
            // Uptime for reboot detection has its own fallback to the console's cached device
            // data. The health write above deliberately nulls uptime when SNMP already reported
            // cpu/mem, which would leave agent sites with no uptime feed at all and therefore no
            // reboot reasons - the console's value is perfectly good for spotting a restart.
            var uptimeForReboot = uptime
                ?? (apiDevice != null ? UniFiDeviceHealthReader.ExtractApiHealth(apiDevice).UptimeSeconds : null);

            if (uptimeForReboot is null or <= 0)
            {
                _logger.LogDebug(
                    "No uptime for {Device} ({Mac}) on site {Slug}: neither the relayed SNMP health nor the console's device data reported one, so no reboot reason can be established",
                    apiDevice?.Name ?? "unknown", health.DeviceMac, connection.SiteSlug);
            }

            // Fall back to the cached profile when the batch-time console lookup came up empty,
            // which is the normal state for the first samples after an agent connects.
            DeviceProbeProfile? profile = null;
            if (_deviceProfilesBySite.TryGetValue(connection.SiteSlug, out var siteProfiles))
                siteProfiles.TryGetValue(NormalizeMac(health.DeviceMac), out profile);

            var rebootDeviceName = apiDevice?.Name ?? profile?.Name;
            if (string.IsNullOrEmpty(rebootDeviceName) &&
                _deviceNamesBySite.TryGetValue(connection.SiteSlug, out var rebootNames))
            {
                rebootNames.TryGetValue(NormalizeMac(health.DeviceMac), out rebootDeviceName);
            }

            var rebootAddress = !string.IsNullOrEmpty(apiDevice?.Ip) ? apiDevice!.Ip : profile?.Address;
            var rebootDeviceType = apiDevice?.DeviceType ?? profile?.DeviceType ?? DeviceType.Unknown;
            var rebootFirmware = apiDevice?.Version ?? profile?.Firmware;

            rebootTracker.RecordUptimeSample(
                health.DeviceMac,
                rebootDeviceName,
                rebootDeviceType,
                rebootAddress,
                uptimeForReboot,
                rebootFirmware,
                timestamp,
                model: apiDevice?.Model);

            // Threshold evaluation through the site's own evaluator instance, same
            // state machine the local medium tier runs. The name cache captured on
            // config push gives the alert a device label instead of a bare MAC.
            // Replayed backlog samples skip evaluation (see AlertFreshness).
            if (DateTime.UtcNow - timestamp > AlertFreshness)
            {
                NoteAlertEvaluationSkipped(connection, timestamp);
                continue;
            }
            try
            {
                string? deviceName = null;
                if (_deviceNamesBySite.TryGetValue(connection.SiteSlug, out var names))
                    names.TryGetValue(NormalizeMac(health.DeviceMac), out deviceName);
                var isGateway = string.Equals(health.DeviceType, "gateway", StringComparison.OrdinalIgnoreCase);
                await _alertRegistry.GetFor(connection.SiteSlug).DeviceHealth.EvaluateAsync(
                    health.DeviceMac, deviceName, health.DeviceType,
                    cpu, mem,
                    temperatureC: temp,
                    tempHighThresholdC: isGateway ? settings?.GatewayTempHighC : settings?.SwitchTempHighC,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Device health alert evaluation failed for {Mac} (site {Slug})",
                    health.DeviceMac, connection.SiteSlug);
            }
        }

        await WriteCustomOidResultsAsync(influx, connection.SiteSlug, batch.CustomOids, deviceByMac, ct);
    }

    /// <summary>
    /// Parses and stores the agent-relayed custom-OID values, mirroring the directly-monitored
    /// medium tier's PollCustomOidsAsync: scalar values land on device_health, walked values land
    /// on interface_counters keyed by the resolved interface name. Aggregated per device / per
    /// interface so all fields for a target share one point.
    /// </summary>
    private async Task WriteCustomOidResultsAsync(
        MonitoringInfluxClient influx,
        string siteSlug,
        IReadOnlyList<SnmpCustomOidResult> results,
        IReadOnlyDictionary<string, UniFiDeviceResponse> deviceByMac,
        CancellationToken ct)
    {
        if (results.Count == 0) return;

        // ifIndex -> ifName per device, from the site's name map (same source the medium tier uses).
        var ifNameByMac = new Dictionary<string, Dictionary<string, string>>();
        if (results.Any(r => r.Scope == 1 && r.InterfaceValues.Count > 0))
        {
            try
            {
                var isDefault = siteSlug == SiteManagementService.DefaultSiteSlug;
                await using var db = _siteDbFactory.CreateForSite(siteSlug, isDefault);
                var maps = await db.InterfaceNameMaps.AsNoTracking()
                    .Where(m => m.IfIndex != null)
                    .Select(m => new { m.DeviceMac, m.IfIndex, m.IfName })
                    .ToListAsync(ct);
                foreach (var m in maps)
                {
                    var mac = NormalizeMac(m.DeviceMac);
                    if (!ifNameByMac.TryGetValue(mac, out var idxMap))
                        ifNameByMac[mac] = idxMap = new Dictionary<string, string>();
                    idxMap[m.IfIndex!.Value.ToString()] = m.IfName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Interface name map load for custom OIDs failed (site {Slug})", siteSlug);
            }
        }

        // Grouped per timestamp as well as per target: a live batch carries one
        // poll's worth of results (all the same stamp, one point per target,
        // same as before), but an agent replaying its store-and-forward backlog
        // after a tunnel outage coalesces many polls into one batch, and each
        // poll must land at its own sample time - not the flush time.
        var deviceFields = new Dictionary<(string Mac, long Ts), Dictionary<string, object>>();
        var deviceTypes = new Dictionary<string, string>();
        var interfaceFields = new Dictionary<(string Mac, string IfName, long Ts), Dictionary<string, object>>();

        foreach (var r in results)
        {
            var mac = NormalizeMac(r.DeviceMac);
            var valueType = (CustomOidValueType)r.ValueType;
            var ts = r.TimestampUnixMs > 0
                ? r.TimestampUnixMs
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (r.Scope == 0) // DeviceLevel
            {
                if (string.IsNullOrEmpty(r.Value)) continue;
                if (!deviceFields.TryGetValue((r.DeviceMac, ts), out var fields))
                {
                    deviceFields[(r.DeviceMac, ts)] = fields = new Dictionary<string, object>();
                    // Custom fields ride the device's existing device_health series, which means
                    // the canonical label - a different spelling here writes them to a series of
                    // their own where nothing reading health will find them.
                    deviceTypes[r.DeviceMac] = deviceByMac.TryGetValue(mac, out var d)
                        ? MonitoringCollectionAgent.DescribeDeviceType(d.DeviceType) : "unknown";
                }
                fields[r.FieldName] = CustomOidValueParser.Parse(r.Value, valueType);
            }
            else // InterfaceLevel
            {
                ifNameByMac.TryGetValue(mac, out var idxMap);
                foreach (var (ifIdx, raw) in r.InterfaceValues)
                {
                    var ifName = idxMap != null && idxMap.TryGetValue(ifIdx, out var n) ? n : ifIdx;
                    var key = (r.DeviceMac, ifName, ts);
                    if (!interfaceFields.TryGetValue(key, out var fields))
                        interfaceFields[key] = fields = new Dictionary<string, object>();
                    fields[r.FieldName] = CustomOidValueParser.Parse(raw, valueType);
                }
            }
        }

        foreach (var ((deviceMac, ts), fields) in deviceFields)
            await influx.WriteCustomFieldsAsync(
                "device_health", deviceMac, fields, deviceTypes.GetValueOrDefault(deviceMac), null, null,
                DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime);

        foreach (var ((deviceMac, ifName, ts), fields) in interfaceFields)
            await influx.WriteCustomFieldsAsync(
                "interface_counters", deviceMac, fields, null, ifName, null,
                DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime);
    }

    /// <summary>
    /// Persists what the hello declared about the agent, so restarts answer before the tunnel is
    /// back (the #1108 durability lesson: the tunnel is behind the thing being asked about). The
    /// reported on-gateway flag is handed to the detector as the authoritative verdict; an
    /// absent flag hands it NOTHING, which is what keeps pre-flag installs on the correlation
    /// path unchanged.
    /// </summary>
    private async Task AdoptHelloFactsAsync(AgentTunnelConnection connection)
    {
        try
        {
            if (connection.OnGateway is { } reported)
                await _onGatewayDetector.NoteReportedAsync(connection.SiteSlug, connection.AgentId, reported);

            var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
            var key = SystemSettingKeys.AgentCapabilitiesFor(connection.AgentId);
            var value = string.Join(",", connection.Capabilities);
            var setting = await db.SystemSettings.FindAsync(key);
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            else if (setting.Value != value)
            {
                setting.Value = value;
                setting.UpdatedAt = DateTime.UtcNow;
            }
            else
                return;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist hello facts for agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>
    /// Pushes the conntrack accounting config to a capable gateway agent: enabled whenever the
    /// site's monitoring is (automatic - no setting of its own), with the site's WAN data-path
    /// interfaces as classification hints. Enabled=false is the fleet-wide kill switch and is
    /// pushed to a capable agent whenever monitoring is off, so a misbehaving runner stops on
    /// the next refresh without an agent update.
    /// </summary>
    public async Task PushConntrackConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        if (!connection.HasCapability(AgentTunnelConnection.ConntrackCapability)) return;
        try
        {
            var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var config = new ConntrackConfig
            {
                Enabled = settings is { Enabled: true },
                // 2s is the agent's floor: a measured pass is ~15 ms on a ~1k-flow table, so this
                // costs under 1% of a gateway core and keeps the live split near-real-time. The
                // agent persists ~6s aggregates (near the SNMP fast tier's grain), stamps every
                // batch with the window it actually covered, and stretches its own cadence when
                // a huge table runs over budget.
                IntervalSeconds = 2,
            };
            if (config.Enabled)
            {
                var wanIfnames = await db.WanProfiles.AsNoTracking()
                    .Where(p => p.DataPathInterface != null && p.DataPathInterface != "")
                    .Select(p => p.DataPathInterface!)
                    .ToListAsync(ct);
                config.WanIfnames.AddRange(wanIfnames.Distinct(StringComparer.OrdinalIgnoreCase));
            }
            connection.TrySend(new ServerMessage { ConntrackConfig = config });
            _logger.LogDebug("Pushed conntrack config (enabled={Enabled}) to agent {Id} (site {Slug})",
                config.Enabled, connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push conntrack config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    // Per-site raw-egress-interface -> UniFi WAN key map from WanProfiles, with the primary's
    // key alongside so its points can omit the wan tag (additive-only for single-WAN installs).
    private readonly ConcurrentDictionary<string, (DateTime At, Dictionary<string, string> ByIfname, string? PrimaryKey)> _wanKeyMaps = new();
    private static readonly TimeSpan WanKeyMapTtl = TimeSpan.FromMinutes(5);

    private async Task<(Dictionary<string, string> ByIfname, string? PrimaryKey)> WanKeyMapAsync(string slug, CancellationToken ct)
    {
        if (_wanKeyMaps.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.At < WanKeyMapTtl)
            return (cached.ByIfname, cached.PrimaryKey);
        var byIfname = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? primaryKey = null;
        try
        {
            await using var db = _siteDbFactory.CreateForSite(slug, slug == SiteManagementService.DefaultSiteSlug);
            foreach (var profile in await db.WanProfiles.AsNoTracking().ToListAsync(ct))
            {
                if (string.IsNullOrEmpty(profile.WanNetworkgroup)) continue;
                var key = GatewayWanHelper.WanInterfaceKeyFromKey(profile.WanNetworkgroup);
                if (profile.IsPrimary == true) primaryKey = key;
                if (!string.IsNullOrEmpty(profile.DataPathInterface))
                    byIfname[profile.DataPathInterface!] = key;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WAN key map load failed for site {Slug}", slug);
        }
        _wanKeyMaps[slug] = (DateTime.UtcNow, byIfname, primaryKey);
        return (byIfname, primaryKey);
    }

    /// <summary>
    /// Records a conntrack batch from a gateway agent. Live batches (every sample window) only
    /// refresh the site's live per-client WAN cache - the measured figures Bandwidth Hogs and
    /// Client Performance read. Aggregated (~6s) batches ride the store-and-forward buffer and
    /// land in the client_wan time series, WAN-tagged via the WanProfiles interface map, plus
    /// one coverage heartbeat point per batch so totals readers can tell measured-idle from
    /// not-covered. An empty batch is a valid statement: the feed is alive and nothing moved.
    /// </summary>
    public async Task RecordConntrackBatchAsync(AgentTunnelConnection connection, ConntrackSampleBatch batch, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(batch.TimestampUnixMs).UtcDateTime;
        var liveStats = _liveStatsRegistry.GetFor(connection.SiteSlug);

        if (!batch.Aggregated)
        {
            var window = Math.Max(1, batch.WindowSeconds);
            // Sum a client's WANs into one live rate; identity resolution mirrors the write path.
            var rates = new Dictionary<string, (long Down, long Up)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in batch.Clients)
            {
                var mac = ResolveConntrackIdentity(connection.SiteSlug, sample);
                var sum = rates.TryGetValue(mac, out var r) ? r : (0L, 0L);
                rates[mac] = (sum.Item1 + sample.WanDownBytes, sum.Item2 + sample.WanUpBytes);
            }
            foreach (var (mac, bytes) in rates)
                liveStats.RecordClientWanRate(mac, bytes.Down * 8.0 / window, bytes.Up * 8.0 / window, timestamp);
            liveStats.NoteConntrackBatch(timestamp, batch.WindowSeconds);
            return;
        }

        var influx = _influxRegistry.GetFor(connection.SiteSlug);
        if (!influx.IsConfigured) await influx.ReconfigureAsync(ct);
        var (wanKeys, primaryKey) = await WanKeyMapAsync(connection.SiteSlug, ct);
        // ONE point per (client, wan tag) per batch, samples summed first. Distinct raw egress
        // interfaces can map to the same tag (the primary and an unknown interface both write
        // untagged), and two points on one series at one timestamp silently overwrite in
        // InfluxDB - which cost whole speed tests, whichever sample happened to write last.
        var sums = new Dictionary<(string Mac, string? Tag), (long Down, long Up, int Flows, long ReconDown, long ReconUp)>();
        foreach (var sample in batch.Clients)
        {
            var mac = ResolveConntrackIdentity(connection.SiteSlug, sample);
            string? wanTag = null;
            if (!string.IsNullOrEmpty(sample.WanIfname) && wanKeys.TryGetValue(sample.WanIfname, out var key)
                && !string.Equals(key, primaryKey, StringComparison.OrdinalIgnoreCase))
                wanTag = key;
            var sum = sums.TryGetValue((mac, wanTag), out var s) ? s : (0L, 0L, 0, 0L, 0L);
            sums[(mac, wanTag)] = (sum.Item1 + sample.WanDownBytes, sum.Item2 + sample.WanUpBytes, sum.Item3 + sample.Flows,
                sum.Item4 + sample.ReconDownBytes, sum.Item5 + sample.ReconUpBytes);
        }
        foreach (var ((mac, wanTag), sum) in sums)
            await influx.WriteClientWanUsageAsync(mac, wanTag,
                sum.Down, sum.Up, batch.WindowSeconds, sum.Flows, timestamp, sum.ReconDown, sum.ReconUp);
        // The coverage heartbeat: written for every aggregated batch, clients or none.
        await influx.WriteClientWanUsageAsync(MonitoringInfluxClient.ClientWanCoverageMarker, null,
            0, 0, batch.WindowSeconds, 0, timestamp);
        // A batch stamped in a past hour is a spool replay (or extreme lag) landing behind the
        // WAN rollup cursor; tell the rollup so the hour re-rolls instead of losing the bytes
        // to the rolled/raw split in long-window reads.
        var nowTicks = DateTime.UtcNow.Ticks;
        if (timestamp.Ticks < nowTicks - nowTicks % TimeSpan.TicksPerHour)
            _usageRollupRegistry.NoteLateClientWanBatch(connection.SiteSlug, timestamp);
    }

    /// <summary>
    /// Who a conntrack sample belongs to. The agent's neighbor-table MAC when it sent one; an
    /// IP-only sample is the gateway's own traffic (or an endpoint the agent could name but not
    /// MAC), matched against the console's device list; anything unresolvable goes to the
    /// explicit unattributed identity, never to a guessed client.
    /// </summary>
    private string ResolveConntrackIdentity(string slug, ConntrackClientSample sample)
    {
        if (!string.IsNullOrEmpty(sample.Mac)) return NormalizeMac(sample.Mac);
        if (string.IsNullOrEmpty(sample.Ip)) return MonitoringInfluxClient.ClientWanUnattributed;
        var console = GetConsoleData(slug);
        foreach (var device in console.Devices)
        {
            if (string.IsNullOrEmpty(device.Mac)) continue;
            if (string.Equals(device.Ip, sample.Ip, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.LanIp, sample.Ip, StringComparison.OrdinalIgnoreCase))
                return NormalizeMac(device.Mac);
        }
        return MonitoringInfluxClient.ClientWanUnattributed;
    }

    /// <summary>Records a batch of probe results from an agent.</summary>
    public async Task RecordBatchAsync(AgentTunnelConnection connection, ProbeResultBatch batch, CancellationToken ct)
    {
        if (batch.Results.Count == 0) return;

        var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;

        // A main-site agent that is not covering the site probes targets the server is probing too,
        // and both write the same series at different cadences - which reads as a sawtooth on the
        // charts rather than as duplicate points. So its results are dropped. (The push path does
        // NOT refuse those targets, which an earlier comment here claimed: the agent is sent the
        // site's targets as an extra vantage point, probes them, and everything it reports lands
        // here to be discarded.)
        //
        // A WAN context's targets are the exception: the server cannot reach the secondary WAN, so
        // it never probes them, and the assigned agent's results are the only measurement there is.
        // Below, each result is judged against the target's own context rather than the whole batch
        // being refused here.
        var agentCoversPrimary = !isDefault || await _agentCoverage.CoversAsync(connection.SiteSlug);
        // Nothing this agent sends can be kept, so drop the batch without loading the site's
        // targets for it - which is what happened before contexts existed, and still happens on
        // every site that has none.
        //
        // The question is whether the agent owns ANY context, not whether it is steered. Those are
        // the same for an agent whose whole box is routed out a WAN, and different for one on the
        // gateway that binds each probe: binding leaves its own route alone, so it is not steered,
        // yet its context's results are still the only measurement that WAN has. Asking the steering
        // question here threw away every result from a gateway vantage the moment it was given an
        // interface to bind.
        if (!agentCoversPrimary && !await AgentOwnsAnyContextAsync(connection, ct))
        {
            _logger.LogDebug(
                "Dropped a batch of {Count} result(s) from agent {Id}: the main site collects for itself and this agent owns no WAN context",
                batch.Results.Count, connection.AgentId);
            return;
        }

        await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault);
        var ids = batch.Results.Select(r => r.TargetId).Distinct().ToList();
        var targets = await db.MonitoringTargets
            .Where(t => ids.Contains(t.TargetId))
            .ToDictionaryAsync(t => t.TargetId, ct);
        var contextsById = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);

        // Distinguishes agent probes from the server's own "server" vantage in
        // the latency measurement; stable across agent renames.
        var vantage = $"agent-{connection.AgentId}";

        // Each site writes to its own buckets (decision D1); the site's client
        // configures itself from that site's MonitoringSettings on first use.
        var influx = _influxRegistry.GetFor(connection.SiteSlug);
        if (!influx.IsConfigured) await influx.ReconfigureAsync(ct);
        // The latency writes below no-op silently on an unconfigured client, so a batch arriving
        // while the site's Influx settings are unreadable - the buffered backlog being the first
        // thing an agent sends after a restart - is swallowed with nothing to show for it. Say so;
        // the batch still runs, because the live caches and alerting do not depend on Influx and
        // are worth having either way.
        if (!influx.IsConfigured)
            _logger.LogWarning(
                "{Count} result(s) from agent {Id} (site {Slug}) will not be stored: the site's InfluxDB client is not configured",
                batch.Results.Count, connection.AgentId, connection.SiteSlug);
        var liveStats = _liveStatsRegistry.GetFor(connection.SiteSlug);
        var discarded = 0;

        foreach (var result in batch.Results)
        {
            if (!targets.TryGetValue(result.TargetId, out var target))
            {
                _logger.LogDebug("Agent {Id} sent result for unknown target {Target}", connection.AgentId, result.TargetId);
                continue;
            }

            var context = target.WanContextId is int contextId && contextsById.TryGetValue(contextId, out var found)
                ? found : null;
            if (!ShouldRecordResult(agentCoversPrimary, context?.AgentId, connection.AgentId))
            {
                discarded++;
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(result.TimestampUnixMs).UtcDateTime;
            var wanContext = context?.InfluxWanTag;

            await influx.WriteLatencyAsync(
                targetId: target.TargetId,
                vantagePoint: vantage,
                targetType: target.TargetType,
                probeMode: target.ProbeMode,
                rttMinMs: result.HasRttMinMs ? result.RttMinMs : null,
                rttAvgMs: result.HasRttAvgMs ? result.RttAvgMs : null,
                rttMaxMs: result.HasRttMaxMs ? result.RttMaxMs : null,
                jitterMs: result.HasJitterMs ? result.JitterMs : null,
                lossPercent: result.LossPercent,
                success: result.Success,
                sent: result.Sent,
                received: result.Received,
                timestamp: timestamp,
                wanContext: wanContext);

            // The site's live caches mirror what the local latency tier
            // records: fabric probes surface on that device's card, and every
            // target's latest result feeds the targets table.
            if (target.TargetType == MonitoringTargetType.Fabric && !string.IsNullOrEmpty(target.DeviceMac))
            {
                liveStats.RecordLatency(target.DeviceMac,
                    result.HasRttAvgMs ? result.RttAvgMs : null,
                    result.LossPercent,
                    timestamp);
            }
            liveStats.RecordTargetProbe(
                target.TargetId,
                result.HasRttAvgMs ? result.RttAvgMs : null,
                result.LossPercent,
                result.Success,
                timestamp);

            // State-change alerting through the site's own evaluator, exactly like
            // the local latency tier: up→down, down→up, sustained loss. The relayed
            // sample is rebuilt into the probe result shape the evaluator consumes.
            // Replayed backlog samples skip evaluation (see AlertFreshness).
            if (DateTime.UtcNow - timestamp <= AlertFreshness)
            {
                try
                {
                    var ping = new PingProbeResult
                    {
                        Target = new ProbeTarget(target.Address, target.ProbeMode, target.Port),
                        Vantage = new ProbeVantage(vantage, VantageKind.Server),
                        Sent = result.Sent,
                        Received = result.Received,
                        Timestamp = timestamp,
                        RttMinMs = result.HasRttMinMs ? result.RttMinMs : null,
                        RttAvgMs = result.HasRttAvgMs ? result.RttAvgMs : null,
                        RttMaxMs = result.HasRttMaxMs ? result.RttMaxMs : null,
                        JitterMs = result.HasJitterMs ? result.JitterMs : null,
                    };
                    await _alertRegistry.GetFor(connection.SiteSlug).Targets.EvaluateAsync(target, ping, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Alert evaluator failed for relayed target {Target} (site {Slug})",
                        target.TargetId, connection.SiteSlug);
                }
            }
            else
            {
                NoteAlertEvaluationSkipped(connection, timestamp);
            }

            if (result.Success)
                target.LastVerified = timestamp;
        }

        if (discarded > 0)
            _logger.LogDebug(
                "Dropped {Count} result(s) from agent {Id}: the main site is collecting for itself and these targets are not in a WAN context this agent owns",
                discarded, connection.AgentId);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Called when a relayed sample is too old for alert evaluation (see
    /// <see cref="AlertFreshness"/>). Right after a reconnect that's the buffered
    /// backlog replaying and stays quiet. On a long-connected tunnel it means the
    /// agent host's clock is behind server time (or samples are arriving badly
    /// delayed), so the site's alerts are silently not firing - surface that with
    /// a rate-limited warning instead of letting it go unnoticed.
    /// </summary>
    private void NoteAlertEvaluationSkipped(AgentTunnelConnection connection, DateTime sampleTimestamp)
    {
        var now = DateTime.UtcNow;
        if (now - connection.ConnectedAt < ReplayGraceAfterConnect) return;
        if (_skewWarnedAt.TryGetValue(connection.SiteSlug, out var warned) && now - warned < SkewWarnInterval) return;
        _skewWarnedAt[connection.SiteSlug] = now;
        _logger.LogWarning(
            "Site {Slug}: samples from agent {AgentId} arrive stamped {BehindMinutes:0} min behind server time and skip alert evaluation - monitoring data still records, but this site's alerts will not fire. Check the agent host's clock/NTP.",
            connection.SiteSlug, connection.AgentId, (now - sampleTimestamp).TotalMinutes);
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');
}

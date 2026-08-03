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
        ILogger<AgentProbeResultSink> logger)
    {
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

    /// <summary>
    /// Called once per connection after the hello exchange, and again by the periodic refresh.
    /// <paramref name="initialConnect"/> separates the two: the console reconnect below belongs to a
    /// genuine connect and must not run on every refresh, or each cycle re-pushes a config that is
    /// already current.
    /// </summary>
    public async Task OnAgentConnectedAsync(AgentTunnelConnection connection, CancellationToken ct, bool initialConnect = false)
    {
        await PushProbeConfigAsync(connection, ct);
        await PushSnmpConfigAsync(connection, ct);
        await PushWanSpeedTestConfigAsync(connection, ct);

        // This site's console reaches the UniFi console THROUGH this agent tunnel.
        // On startup / after an agent restart the console auto-connect can run
        // before the tunnel is up, exhaust its short retry window, and stay
        // disconnected until a manual reconnect. Now that the tunnel is up,
        // reconnect it - fire-and-forget so we never block the tunnel read loop.
        if (initialConnect)
            _ = ReconnectConsoleIfViaAgentAsync(connection);
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

    private async Task ReconnectConsoleIfViaAgentAsync(AgentTunnelConnection connection)
    {
        try
        {
            // The 60s config refresh also lands here while a black-holed tunnel is
            // still registered (the 90s watchdog hasn't reaped it). Reconnecting the
            // console through a tunnel that's silent past the stale threshold just
            // dials the dead loopback proxy and clobbers the awaiting-agent state the
            // proxy's unreachable signal set. Skip; a real agent reconnect arrives
            // with a fresh LastMessageAt and proceeds normally.
            if (connection.IsStale)
                return;

            var siteConnection = _siteConnections.GetFor(connection.SiteSlug);
            if (!siteConnection.IsConnected && await siteConnection.IsConsoleViaAgentAsync())
            {
                _logger.LogInformation(
                    "Agent tunnel up for site {Slug}; reconnecting its console via the tunnel", connection.SiteSlug);
                await siteConnection.ReconnectAsync();
            }

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
                await PushSnmpConfigAsync(connection, CancellationToken.None);
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

            var targets = await db.MonitoringTargets
                .AsNoTracking()
                .Where(t => t.Enabled)
                .ToListAsync(ct);
            var contextsById = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);

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
            var selfAddress = await _onGatewayDetector.IsAgentOnGatewayAsync(connection.SiteSlug)
                ? _onGatewayDetector.LastKnownAgentIp(connection.SiteSlug)
                : null;
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
                // Targets in an agent-assigned WAN context go only to that agent
                // (typically a probe-only instance bound behind the right WAN);
                // unassigned targets go to every agent as extra vantage points.
                if (target.WanContextId is int contextId
                    && contextsById.TryGetValue(contextId, out var context)
                    && context.AgentId is int assignedAgent
                    && assignedAgent != connection.AgentId)
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
                });
            }

            connection.TrySend(new ServerMessage { ProbeConfig = config });
            _logger.LogInformation("Pushed {Count} probe target(s) to agent {Id} (site {Slug}){Skipped}",
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
    /// Pushes the WAN speed-test server list (global, main database) so the
    /// agent can serve its /wan/ redirect without the external servers needing
    /// any per-site config: /wan/ goes to the default server, /wan/&lt;id&gt;/ to
    /// that mapped server. Pushed on connect and by the periodic refresh so
    /// Settings edits reach connected agents.
    /// </summary>
    public async Task PushWanSpeedTestConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
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
    /// polling those devices and pushing a second poller would double every sample.
    /// </summary>
    public async Task PushSnmpConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
        if (isDefault && !await _agentCoverage.CoversAsync(connection.SiteSlug)) return;
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
                        DeviceType = device.DeviceType.ToString().ToLowerInvariant(),
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
                _logger.LogInformation("Pushed SNMP config with {Count} device(s) to agent {Id} (site {Slug})",
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
                liveStats.RecordPortRate(sample.DeviceMac, sample.IfName, calc.RateOutBps.Value, calc.RateInBps.Value, timestamp);

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
                    meshUplink[NormalizeMac(sample.DeviceMac)] = (calc.RateInBps.Value, calc.RateOutBps.Value);
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
                timestamp: timestamp);

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
                timestamp);

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
                    deviceTypes[r.DeviceMac] = deviceByMac.TryGetValue(mac, out var d)
                        ? d.DeviceType.ToString() : "unknown";
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

    /// <summary>Records a batch of probe results from an agent.</summary>
    public async Task RecordBatchAsync(AgentTunnelConnection connection, ProbeResultBatch batch, CancellationToken ct)
    {
        if (batch.Results.Count == 0) return;

        var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;

        // The push path already refuses to send targets to a main-site agent that is not covering
        // the site; results are refused for the same reason. Switching coverage off stops the
        // config going out but does not stop an agent that already has targets, so it keeps
        // probing and pushing while the server resumes probing the same targets itself. Both write
        // the same series at different cadences, which reads as a sawtooth on the charts rather
        // than as duplicate points.
        if (isDefault && !await _agentCoverage.CoversAsync(connection.SiteSlug)) return;

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
        var liveStats = _liveStatsRegistry.GetFor(connection.SiteSlug);

        foreach (var result in batch.Results)
        {
            if (!targets.TryGetValue(result.TargetId, out var target))
            {
                _logger.LogDebug("Agent {Id} sent result for unknown target {Target}", connection.AgentId, result.TargetId);
                continue;
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(result.TimestampUnixMs).UtcDateTime;
            var wanContext = target.WanContextId is int contextId && contextsById.TryGetValue(contextId, out var context)
                ? context.Name : null;

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

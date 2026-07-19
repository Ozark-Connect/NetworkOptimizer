using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The monitoring collection agent (spec 5.2). The scheduled runner that polls SNMP, the
/// UniFi API, and SFP data, writing the schema-aligned results to the dedicated InfluxDB
/// instance.
///
/// Three-tier polling:
///   * Fast (default 5 s) — interface counters, with server-side rate computation.
///   * Medium (default 30 s) — device health: CPU, memory, temperature, uptime.
///   * Slow (default 300 s) — static metadata: ifName, ifAlias, ifSpeed → reconcile the
///     InterfaceNameMap relational table (spec 3.7).
///
/// The agent activates only when monitoring is enabled, SNMP detection succeeded, and the
/// InfluxDB client reports healthy. Otherwise it sleeps and re-checks each tick.
///
/// Credentials come from MonitoringSettings (populated by SnmpDetectionService); the agent
/// itself never stores them independently.
///
/// One instance exists per site, owned by <see cref="MonitoringCollectionRegistry"/>.
/// A non-default instance reads settings, targets, and relational rows from its own
/// site's database and writes to that site's Influx buckets, console connection, and
/// live-stats cache. When the site has a connected on-site agent, the tunnel relay
/// already covers latency probing and SNMP polling from inside that network, so the
/// local loops skip those and keep only the console-API-driven work (WiFi client
/// snapshots, SFP DDM, API health fallback, fabric target reconciliation).
/// </summary>
public class MonitoringCollectionAgent : BackgroundService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly UniFiConnectionService _connectionService;
    private readonly MonitoringInfluxClient _influx;
    private readonly MonitoringLiveStats _liveStats;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly LocalProbeExecutor _localProbe;
    private readonly NetworkOptimizer.Web.Services.Monitoring.MonitoringAlertEvaluator _alertEvaluator;
    private readonly NetworkOptimizer.Web.Services.Monitoring.SfpAlertEvaluator _sfpAlertEvaluator;
    private readonly NetworkOptimizer.Web.Services.Monitoring.DeviceHealthAlertEvaluator _deviceHealthAlertEvaluator;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MonitoringCollectionAgent> _logger;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly string _siteSlug;
    private readonly bool _isDefault;

    // Throttles the agent-site console reconnect done inside ReconcileFabricTargetsAsync.
    private DateTime _lastConsoleEnsureAt;

    // Counter delta cache for server-side rate computation. Key = "deviceMac/ifName".
    private readonly ConcurrentDictionary<string, InterfaceRateCalculator.State> _counterCache = new();
    // Per-target last-probed time, for per-target poll intervals on a shared loop.
    private readonly ConcurrentDictionary<int, DateTime> _targetLastProbed = new();

    // SNMP failure counting + temporary exclusion (see SnmpFailureTracker for the
    // rationale). Keyed by normalized device MAC.
    private readonly SnmpFailureTracker _snmpFailures = new();
    // Last successful SNMP poll per device (normalized MAC -> UTC). Drives the
    // "last polled" column and "not yet polled" state on the Setup dashboard.
    private readonly ConcurrentDictionary<string, DateTime> _snmpLastPolled = new();

    // SNMP self-heal throttle. When a majority of SNMP-enabled devices are failing (the
    // symptom of a community string rotated in UniFi), we re-pull the SNMP config from
    // the console and adopt it if it changed. The re-pull is one cheap, diff-gated
    // console call, so we react eagerly. Two guards keep habitually-failing devices from
    // re-pulling forever: a hard floor between any two re-pulls, and a long idle backoff
    // once the same devices keep failing (a device problem, not a credential change).
    // Escalation - more devices failing than last time, or a standing too-long-community
    // verdict - bypasses the backoff so a genuine flip is caught within a couple of cycles.
    private DateTime _lastSnmpSelfHealAt = DateTime.MinValue;
    private int _lastSnmpSelfHealFailingCount;
    private static readonly TimeSpan SnmpSelfHealMinInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SnmpSelfHealIdleBackoff = TimeSpan.FromMinutes(15);
    // When the fast tier first actually polls (console up, poller built). Self-heal
    // stays quiet for a short warm-up after this so first-cycle jitter can't fire a
    // needless re-pull before devices have had a fair chance to answer. Kept short (a
    // few poll cycles) so it doesn't slow the genuine cold-start-wrong-community heal -
    // the 2-consecutive-failure requirement and the diff-gate are the real guards.
    private DateTime _snmpPollingStartedAt = DateTime.MinValue;
    private static readonly TimeSpan SnmpSelfHealWarmup = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Set when the self-heal re-pull found the console's community string over the
    /// device-supported max (nothing can be adopted; polling stays down until the user
    /// shortens it). The Monitoring page reads this on its refresh tick so the Live View
    /// banner can name the real problem instead of pointing at the SNMP device table.
    /// Cleared whenever polling is healthy or a usable config is adopted.
    /// </summary>
    public bool CommunityTooLongDetected { get; private set; }

    /// <summary>
    /// Lets the Setup page's interactive re-check override the cached self-heal sighting
    /// with its fresher console read. Without this, a user who shortens the community and
    /// hits Re-check still sees the too-long banner until the agent's next re-pull.
    /// </summary>
    public void NoteExternalDetection(bool communityTooLong) =>
        CommunityTooLongDetected = communityTooLong;

    // Custom OID config cache. Refreshed every medium-tier cycle.
    private Dictionary<string, List<CustomOidConfiguration>> _customOidsByDevice = new();
    private DateTime _customOidsLoadedAt = DateTime.MinValue;
    private static readonly TimeSpan CustomOidsCacheTtl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _snmpGate = new(8);

    public MonitoringCollectionAgent(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory siteDbFactory,
        AgentTunnelRegistry tunnelRegistry,
        SiteConnectionRegistry siteConnections,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringLiveStatsRegistry liveStatsRegistry,
        ICredentialProtectionService credentialProtection,
        LocalProbeExecutor localProbe,
        MonitoringAlertRegistry alertRegistry,
        Licensing.LicenseStateService licenseState,
        ILoggerFactory loggerFactory,
        ILogger<MonitoringCollectionAgent> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _tunnelRegistry = tunnelRegistry;
        _licenseState = licenseState;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _isDefault = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _connectionService = siteConnections.GetFor(_siteSlug);
        _influx = influxRegistry.GetFor(_siteSlug);
        _liveStats = liveStatsRegistry.GetFor(_siteSlug);
        _credentialProtection = credentialProtection;
        _localProbe = localProbe;
        var evaluators = alertRegistry.GetFor(_siteSlug);
        _alertEvaluator = evaluators.Targets;
        _sfpAlertEvaluator = evaluators.Sfp;
        _deviceHealthAlertEvaluator = evaluators.DeviceHealth;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSiteDbAsync(CancellationToken ct)
    {
        if (!_isDefault)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    /// <summary>
    /// Whether a connected on-site agent is collecting for this site right now. The
    /// tunnel relay handles latency probing and SNMP polling from inside the site's
    /// network (where the server often has no reach), so the local loops for those
    /// paths stand down while an agent is connected. Never true for the default site:
    /// its agents are additional vantage points, not replacements for local collection.
    /// </summary>
    private bool AgentCoversCollection() =>
        !_isDefault && (_tunnelRegistry.GetForSite(_siteSlug).Count > 0 || _siteAgentEnrolled);

    // A secondary site is agent-backed if it has an ENROLLED agent, even while that agent
    // is momentarily disconnected (app startup, agent reconnect). The NO Server can't reach
    // such a site's network, so its local latency/SNMP loops must stand down the whole
    // time - not only while the tunnel is live. Gating solely on a live tunnel meant that on
    // every NO-server startup, before the agent reconnected, the server probed the site's
    // targets from its own stack: false LAN packet-loss, and the server's own RTT on shared
    // (anycast) targets. Refreshed on a throttle from the tier loop.
    private volatile bool _siteAgentEnrolled;
    private DateTime _agentCoverageCheckedAt = DateTime.MinValue;
    private static readonly TimeSpan AgentCoverageTtl = TimeSpan.FromSeconds(30);

    private async Task RefreshAgentCoverageAsync(CancellationToken ct)
    {
        if (_isDefault) return;
        if (DateTime.UtcNow - _agentCoverageCheckedAt < AgentCoverageTtl) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == _siteSlug, ct);
            var wasEnrolled = _siteAgentEnrolled;
            _siteAgentEnrolled = site != null && await db.SiteAgents.AsNoTracking()
                .AnyAsync(a => a.SiteId == site.Id && a.Enabled && a.EnrolledAt != null, ct);
            _agentCoverageCheckedAt = DateTime.UtcNow;

            // The moment an external site first gains an agent, activate the default internet
            // targets that were seeded disabled while it had none - the agent can now probe them
            // from inside the site (AgentProbeResultSink only pushes enabled targets).
            if (!wasEnrolled && _siteAgentEnrolled)
                await EnableSeededDefaultTargetsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent coverage refresh failed for site {Slug}", _siteSlug);
        }
    }

    /// <summary>
    /// No-op. Owned by MonitoringCollectionRegistry (started/stopped via
    /// Start/StopAsync), but handed to components through a scoped forwarding
    /// registration - so the DI container would otherwise call Dispose at every
    /// request/circuit scope end, and BackgroundService.Dispose cancels the
    /// stopping token, silently killing this site's collection loops. The
    /// registry owns the real lifecycle.
    /// </summary>
    public override void Dispose() { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Monitoring collection agent starting (site {Site})", _siteSlug);

        // Resolve agent coverage before seeding so an external (non-default) site seeds its
        // default internet targets disabled until an agent is deployed - otherwise the central
        // server, which can't see the site's network, would probe anycast DNS from its own
        // stack and log that as the site's ISP latency. No-op on the default site (always seeds
        // enabled), where this returns immediately.
        await RefreshAgentCoverageAsync(stoppingToken);

        // Seed default targets on startup so the latency tier has something to probe even
        // before the upstream wizard runs. Safe to call repeatedly — only inserts if absent.
        try { await SeedDefaultTargetsAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Default target seeding failed"); }

        // Warm the live SFP cache from the latest persisted DDM points so the Optical
        // tables aren't blank between a restart and the site's first successful slow
        // tick - up to several minutes on agent-backed sites, whose first tick usually
        // fires before the tunnel console reconnects. Live readings always win: the
        // seed never overwrites an entry the slow tier has already recorded.
        try { await SeedSfpLiveCacheAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogDebug(ex, "SFP live-cache seeding failed (site {Site})", _siteSlug); }

        // Four independent loops, slightly staggered to avoid burst overlap.
        var fastTask = RunTierAsync("fast", GetFastInterval, FastTierCollectAsync, TimeSpan.FromSeconds(5), stoppingToken);
        var mediumTask = RunTierAsync("medium", GetMediumInterval, MediumTierCollectAsync, TimeSpan.FromSeconds(10), stoppingToken);
        var slowTask = RunTierAsync("slow", GetSlowInterval, SlowTierCollectAsync, TimeSpan.FromSeconds(15), stoppingToken);
        // The latency tier ticks every 2 seconds and probes only the targets whose own
        // per-target poll interval has elapsed since their last probe. One loop, many
        // independent cadences — cheaper than maintaining a timer per target.
        var latencyTask = RunTierAsync("latency",
            _ => TimeSpan.FromSeconds(2),
            LatencyTierCollectAsync,
            TimeSpan.FromSeconds(20),
            stoppingToken);
        // Periodic InfluxDB health revalidation. The Test button on the Monitoring page
        // is fine for the user-initiated case, but we want the persisted status to flip
        // automatically when the token gets revoked or buckets get deleted out from
        // under us. Every 60s the agent calls CheckHealthAsync, which now exercises the
        // bucket via a Flux query instead of just /ping (see MonitoringInfluxClient).
        var healthTask = RunTierAsync("health",
            _ => TimeSpan.FromSeconds(60),
            HealthTierCollectAsync,
            TimeSpan.FromSeconds(25),
            stoppingToken);
        // WiFi client snapshots from the UniFi stat/sta API on a 30s cadence (spec 5.2).
        // Per-AP aggregates feed MonitoringLiveStats for the live map; raw points land
        // in InfluxDB's wifi_client measurement for timeline / drill-down. Cardinality
        // control: AP MAC + band are tags, client MAC is a field.
        var wifiTask = RunTierAsync("wifi",
            _ => TimeSpan.FromSeconds(30),
            WifiClientTierCollectAsync,
            TimeSpan.FromSeconds(30),
            stoppingToken);
        // SNMP credential self-heal on its own short cadence. It must NOT ride the fast
        // tier's cycle: when every SNMP call is timing out (the exact failure it exists to
        // detect), a fast cycle stretches to minutes of stacked timeouts and an end-of-cycle
        // check starves - the self-heal took 4+ minutes to fire. This loop reads the failure
        // tracker and (rarely, throttled) makes one console API call, so 10s is cheap.
        var selfHealTask = RunTierAsync("snmp-selfheal",
            _ => TimeSpan.FromSeconds(10),
            SnmpSelfHealTierAsync,
            TimeSpan.FromSeconds(12),
            stoppingToken);

        await Task.WhenAll(fastTask, mediumTask, slowTask, latencyTask, healthTask, wifiTask, selfHealTask);
        _logger.LogInformation("Monitoring collection agent stopped (site {Site})", _siteSlug);
    }

    private TimeSpan GetFastInterval(MonitoringSettings s) =>
        TimeSpan.FromSeconds(Math.Max(2, s.FastPollIntervalSeconds));
    private TimeSpan GetMediumInterval(MonitoringSettings s) =>
        TimeSpan.FromSeconds(Math.Max(10, s.MediumPollIntervalSeconds));
    private TimeSpan GetSlowInterval(MonitoringSettings s) =>
        TimeSpan.FromSeconds(Math.Max(60, s.SlowPollIntervalSeconds));

    private async Task RunTierAsync(
        string tierName,
        Func<MonitoringSettings, TimeSpan> intervalSelector,
        Func<MonitoringSettings, CancellationToken, Task> collect,
        TimeSpan initialDelay,
        CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval = TimeSpan.FromSeconds(60);
            try
            {
                // Keep agent-coverage state fresh so a secondary site's local loops stand
                // down for an enrolled-but-reconnecting agent (no startup false loss).
                await RefreshAgentCoverageAsync(stoppingToken);
                var settings = await LoadSettingsAsync(stoppingToken);
                if (settings == null || !await ShouldRunNowAsync(settings, stoppingToken))
                {
                    // Not enabled or not configured — sleep and re-check
                    interval = TimeSpan.FromSeconds(30);
                }
                else
                {
                    interval = intervalSelector(settings);
                    await collect(settings, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring {Tier} tier collection failed (site {Site})", tierName, _siteSlug);
                interval = TimeSpan.FromSeconds(30);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<MonitoringSettings?> LoadSettingsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            return await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load MonitoringSettings");
            return null;
        }
    }

    private async Task<bool> ShouldRunNowAsync(MonitoringSettings settings, CancellationToken ct)
    {
        // License enforcement: restricted sites collect nothing. The registry
        // stops this instance within a reconcile cycle; this closes the window.
        if (!_licenseState.IsSiteOperational(_siteSlug)) return false;
        if (!settings.Enabled) return false;
        if (settings.SnmpDetectionState != SnmpDetectionState.EnabledV2c
            && settings.SnmpDetectionState != SnmpDetectionState.EnabledV3Only
            && settings.SnmpDetectionState != SnmpDetectionState.Working)
            return false;
        // The default site stores its own InfluxDB token. Secondary sites derive
        // their InfluxDB config from main (shared server/token, per-site buckets),
        // so their MonitoringSettings token is empty - fall back to whether the
        // effective per-site client is configured (deriving it if needed). Without
        // this, every agent site's collection agent (fabric target reconcile, device
        // and wifi tiers feeding Device Stats) was gated off entirely.
        if (!string.IsNullOrEmpty(settings.InfluxDbToken)) return true;
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);
        return _influx.IsConfigured;
    }

    // ---- Tier collection methods ----

    private async Task FastTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        // A connected on-site agent streams interface counters over the tunnel; polling
        // the same devices from here would double-write (and usually can't reach them).
        if (AgentCoversCollection()) return;

        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        var poller = GetOrBuildPoller(settings);
        if (poller == null) return;

        // Mark the first real poll cycle so the self-heal warm-up grace can start.
        if (_snmpPollingStartedAt == default) _snmpPollingStartedAt = DateTime.UtcNow;

        // Configure InfluxDB client (no-op if already configured)
        if (!_influx.IsConfigured)
            await _influx.ReconfigureAsync(ct);

        // Update the per-port rate cache from the UniFi API port_table for every
        // switch / gateway. Used below to compute AP backhaul rates from the upstream
        // port the AP is plugged into (spec 5.6).
        _fabric.UpdateUnifiPortRates(devices, DateTime.UtcNow);

        // Resolve the gateway LAN IP once per cycle so the SNMP poll targets the
        // LAN-side address (which actually answers) instead of UniFi's reported WAN
        // public IP for the gateway (which never will).
        var gatewayLanIp = await ResolveGatewayLanIpAsync(ct);

        var deviceTasks = devices.Select(async device =>
        {
            await _snmpGate.WaitAsync(ct);
            try
            {
                var mac = NormalizeMac(device.Mac);

                // UniFi requires snmp_location or snmp_contact to be set for
                // SNMP to be enabled on a device. Both empty/null = SNMP off.
                if (!Monitoring.SnmpDeviceRules.HasSnmpEnabled(device))
                    return;

                if (IsSnmpExcluded(mac))
                    return;
                try
                {
                    var pollIp = ResolveSnmpAddress(device, gatewayLanIp);
                    if (!IPAddress.TryParse(pollIp, out var ip)) return;
                    var interfaces = await poller.GetInterfaceMetricsAsync(ip, device.Name);
                    var now = DateTime.UtcNow;
                    double aggregateInBps = 0;
                    double aggregateOutBps = 0;
                    bool anyRate = false;
                    // For mesh APs, the "vwiresta*" SNMP interface is the virtual
                    // wireless station - the AP acting as a client to its parent.
                    // Every byte the AP shuttles over the wireless backhaul flows
                    // through this interface, so its ifInOctets / ifOutOctets is
                    // the most direct boundary measurement we can get.
                    double? apMeshUplinkInBps = null;
                    double? apMeshUplinkOutBps = null;
                    foreach (var iface in interfaces)
                    {
                        var (rateIn, rateOut) = WriteInterfaceCounters(device, iface, now);
                        if (rateIn.HasValue && rateOut.HasValue)
                        {
                            anyRate = true;
                            if (LanFabricAggregator.IncludeInFabricSum(device.DeviceType, iface.Description))
                            {
                                aggregateInBps += rateIn.Value;
                                aggregateOutBps += rateOut.Value;
                            }
                            if (device.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.AccessPoint
                                && !string.IsNullOrEmpty(iface.Description)
                                && iface.Description.StartsWith("vwiresta", StringComparison.OrdinalIgnoreCase)
                                && !iface.Description.Contains('.'))
                            {
                                apMeshUplinkInBps = rateIn.Value;
                                apMeshUplinkOutBps = rateOut.Value;
                            }
                        }
                    }
                    if (anyRate)
                    {
                        // Successful SNMP poll - reset the failure counter. APs are
                        // the only fabric-sum holdout: their radio "interfaces"
                        // over-count beacons / retries / MIMO duplicates so the sum
                        // doesn't represent useful payload. Switches, gateways and
                        // cellular modems all see sum(rx)/sum(tx) as a coherent
                        // fabric I/O total - ShouldMonitor() already strips loopback,
                        // tunnels and bridges, so the surviving interfaces are
                        // physical ports.
                        _snmpFailures.NoteSuccess(mac);
                        _snmpLastPolled[mac] = now;
                        if (device.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Switch
                            || device.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Gateway
                            || device.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.CellularModem)
                        {
                            _liveStats.RecordFabricSum(device.Mac, aggregateInBps, aggregateOutBps, now);
                        }
                        if (apMeshUplinkInBps.HasValue && apMeshUplinkOutBps.HasValue)
                        {
                            // vwiresta rateIn (ifInOctets) = bytes received over the mesh
                            // backhaul = downloads. rateOut = bytes transmitted = uploads.
                            // Our aggregate convention (per AP tooltip semantics: "Ingress"
                            // = bytes the AP receives from its wifi clients = uploads) puts
                            // uploads on aggregateInBps and downloads on aggregateOutBps.
                            _liveStats.RecordInterfaceAggregate(device.Mac, apMeshUplinkOutBps.Value, apMeshUplinkInBps.Value, now);
                        }
                    }
                    else
                    {
                        // SNMP returned no usable data. If the device is otherwise reachable
                        // (recent fabric ping succeeded), it likely doesn't support SNMP;
                        // count the failure and maybe exclude it.
                        NoteSnmpFailure(mac);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Fast-tier interface poll failed for {Device}", device.Mac);
                    NoteSnmpFailure(mac);
                }
            }
            finally { _snmpGate.Release(); }
        });
        await Task.WhenAll(deviceTasks);

        // Post-process: override device aggregates with their parent-uplink-port
        // counters. For APs (spec 5.6) we already did this. For switches and gateways,
        // summing every interface counter on the device double-counts traffic that
        // crosses the switch fabric port-to-port and includes purely local traffic
        // that never crosses the uplink - both of which inflate the "device activity"
        // the topology cares about. The trunk/uplink port is the boundary between
        // the device and the rest of the network, which is what the topology pipe
        // actually carries.
        // Topology-boundary aggregates: AP/switch uplink-port rates, mesh-AP
        // synthesis, and gateway WAN rates, with all the direction conventions and
        // fallbacks. Shared verbatim with the agent-relayed path (AgentProbeResultSink)
        // via LanFabricAggregator so secondary sites compute identical numbers.
        _fabric.WriteAggregates(devices, _liveStats, DateTime.UtcNow);
    }

    /// <summary>
    /// The dedicated self-heal tier: evaluates the failure tracker against the current
    /// device list every tick, independent of the fast tier's cycle time. Uses the same
    /// cached device list as the pollers (4s TTL), so a tick normally costs a dictionary
    /// scan and nothing else.
    /// </summary>
    private async Task SnmpSelfHealTierAsync(MonitoringSettings settings, CancellationToken ct)
    {
        if (AgentCoversCollection()) return;
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;
        await MaybeSelfHealSnmpAsync(devices, settings, ct);
    }

    /// <summary>
    /// Self-heals stale SNMP credentials. SNMPv2c gives no distinct "wrong community"
    /// error - a rotated community is indistinguishable from a timeout - so the trigger
    /// is behavioural: a majority of the SNMP-enabled devices are failing. When that
    /// happens we re-pull the SNMP config from the console API (which still answers even
    /// when SNMP to the devices is dead, different transport) and adopt it only if it
    /// actually differs from what we're using. A real outage leaves UniFi's config
    /// untouched, so same config = no-op. Covers a rotated v2c community, changed v3
    /// credentials, and SNMP being disabled entirely. A community longer than the
    /// device-supported max is left alone - re-pulling returns the same broken value,
    /// and the Setup tab already surfaces the length warning.
    /// </summary>
    private async Task MaybeSelfHealSnmpAsync(
        IReadOnlyList<UniFiDeviceResponse> devices, MonitoringSettings settings, CancellationToken ct)
    {
        if (settings.SnmpDetectionState != SnmpDetectionState.EnabledV2c
            && settings.SnmpDetectionState != SnmpDetectionState.EnabledV3Only
            && settings.SnmpDetectionState != SnmpDetectionState.Working)
            return;

        // Denominator: the devices UniFi reports as SNMP-enabled (location/contact set).
        // Basing the trigger on the current device list - NOT a "previously-healthy" set -
        // is what makes it fire even on a cold start where the community was already wrong
        // before this process began (nothing ever polled successfully, so a healthy
        // baseline is permanently empty). This was the bug: a restart during an outage
        // could never self-heal.
        var snmpMacs = devices
            .Where(d => Monitoring.SnmpDeviceRules.HasSnmpEnabled(d))
            .Select(d => NormalizeMac(d.Mac))
            .Where(m => m.Length > 0)
            .ToList();
        if (snmpMacs.Count == 0) return;

        // Warm-up grace: ignore the first poll cycles after startup, where transient
        // failures (network/console still settling) shouldn't fire a re-pull. A correct
        // community answers immediately, so this only delays a genuinely-wrong cold start.
        if (_snmpPollingStartedAt == default
            || DateTime.UtcNow - _snmpPollingStartedAt < SnmpSelfHealWarmup)
            return;

        // Trigger: a majority of SNMP-enabled devices failing (>= 2 consecutive misses
        // each, so a single dropped packet doesn't count). A majority is a fabric-wide
        // problem (community rotated / SNMP disabled); a minority is a device problem
        // (a Flex Mini that can't do SNMP) and must NOT re-pull. On a single-device
        // network the majority is that one device. The re-pull is cheap and diff-gated,
        // so a genuine outage just no-ops.
        var failing = snmpMacs.Count(mac => _snmpFailures.IsFailing(mac, minConsecutiveFailures: 2));
        var threshold = Math.Max(1, (int)Math.Ceiling(snmpMacs.Count * 0.5));
        if (failing < threshold)
        {
            // Fabric is healthy again - forget the baseline so the next failure event is
            // treated as fresh and re-pulls promptly instead of waiting out the backoff.
            _lastSnmpSelfHealFailingCount = 0;
            CommunityTooLongDetected = false;
            return;
        }

        var sinceLast = DateTime.UtcNow - _lastSnmpSelfHealAt;
        if (sinceLast < SnmpSelfHealMinInterval) return;

        // Past the floor: only re-pull again if MORE devices are failing than at our last
        // check. If it's the same devices still down, we already confirmed the config
        // didn't change (a device problem, not a credential rotation), so wait out the
        // idle backoff rather than re-pulling for habitually-failing devices every cycle.
        // Exception: while the last verdict was a too-long community, keep the short
        // cadence - the user is presumably out shortening it, and the fix must be
        // adopted ~30s after devices reprovision, not after a 15-minute backoff.
        bool escalated = failing > _lastSnmpSelfHealFailingCount || CommunityTooLongDetected;
        if (!escalated && sinceLast < SnmpSelfHealIdleBackoff) return;

        if (_connectionService.Client == null || !_connectionService.IsConnected)
            return; // can't reach the console; retry once reconnected (interval not consumed)

        _lastSnmpSelfHealAt = DateTime.UtcNow;
        _lastSnmpSelfHealFailingCount = failing;

        _logger.LogWarning(
            "SNMP self-heal (site {Site}): {Failing}/{Total} SNMP-enabled devices failing polls. Re-pulling config from UniFi to check for a credential change.",
            _siteSlug, failing, snmpMacs.Count);

        try
        {
            SnmpDetectionResult detected;
            using (var raw = await _connectionService.Client.GetSettingsRawAsync(ct))
            {
                if (raw == null) return;
                detected = SnmpDetectionService.ParseSnmpSettings(raw);
            }
            if (!detected.Success) return;
            CommunityTooLongDetected = detected.CommunityTooLong;

            // A too-long community re-pulls to the same value the devices already reject;
            // adopting it would change nothing and we'd loop. Leave it for the user.
            if (detected.CommunityTooLong)
            {
                _logger.LogWarning(
                    "SNMP self-heal (site {Site}): UniFi Community String is {Len} chars, over the reliable {Max}-char device max. Not adopting - it must be shortened.",
                    _siteSlug, detected.Community?.Length, SnmpDetectionResult.MaxSupportedCommunityLength);
                return;
            }

            if (!SnmpDetectionService.ConfigDiffers(settings, detected, _credentialProtection))
            {
                _logger.LogInformation(
                    "SNMP self-heal (site {Site}): UniFi SNMP config is unchanged, so the failures are not a credential change (device outage, firewall, or IPS). Leaving polling as-is.",
                    _siteSlug);
                return;
            }

            await using var db = await CreateSiteDbAsync(ct);
            var row = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (row == null) return;
            var before = row.SnmpDetectionState;
            SnmpDetectionService.ApplyToSettings(row, detected, _credentialProtection);
            row.LastSnmpDetection = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // Clear failure/exclusion state so the dropped devices are retried at once
            // with the new credentials. The poller rebuilds itself next cycle because
            // ComputePollerConfigHash keys on the credential fields we just changed.
            _snmpFailures.Reset();

            _logger.LogWarning(
                "SNMP self-heal (site {Site}): adopted updated SNMP config from UniFi ({Before} -> {After}). Reset failure tracking; polling resumes with the new credentials.",
                _siteSlug, before, row.SnmpDetectionState);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SNMP self-heal (site {Site}): re-pull failed", _siteSlug);
        }
    }

    // Owns the per-port / per-device byte-rate caches and the topology-boundary
    // aggregate computation, shared verbatim with the agent-relayed path
    // (AgentProbeResultSink) so secondary sites compute identical numbers. This agent
    // is per-site, so one instance is correct.
    private readonly LanFabricAggregator _fabric = new();


    private async Task MediumTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        // With an on-site agent connected, SNMP health arrives over the tunnel relay.
        // The local pass keeps only the UniFi-API fallback below, restricted to devices
        // the agent's SNMP runner won't cover (those without SNMP enabled).
        var agentCovers = AgentCoversCollection();
        if (agentCovers)
        {
            if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);
            await CollectApiHealthFallbackAsync(settings, devices,
                snmpHealthHits: new ConcurrentDictionary<string, bool>(),
                snmpTempHits: new ConcurrentDictionary<string, bool>(),
                snmpHandledElsewhere: true);
            return;
        }

        var poller = GetOrBuildPoller(settings);
        if (poller == null) return;
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);

        var gatewayLanIp = await ResolveGatewayLanIpAsync(ct);
        var customOids = await LoadCustomOidsAsync(ct);
        var snmpHealthHits = new ConcurrentDictionary<string, bool>();
        var snmpTempHits = new ConcurrentDictionary<string, bool>();
        var deviceTasks = devices.Select(async device =>
        {
            await _snmpGate.WaitAsync(ct);
            try
            {
                if (!Monitoring.SnmpDeviceRules.HasSnmpEnabled(device))
                    return;
                if (IsSnmpExcluded(NormalizeMac(device.Mac))) return;
                var pollIp = ResolveSnmpAddress(device, gatewayLanIp);
                if (!IPAddress.TryParse(pollIp, out var ip)) return;
                var metrics = await poller.GetDeviceMetricsAsync(ip, device.Name);
                if (!metrics.IsReachable)
                {
                    NoteSnmpFailure(NormalizeMac(device.Mac));
                    return;
                }

                _snmpLastPolled[NormalizeMac(device.Mac)] = DateTime.UtcNow;

                var cpu = metrics.CpuUsage > 0 ? metrics.CpuUsage : (double?)null;
                var memPct = metrics.MemoryUsage > 0 ? metrics.MemoryUsage : (double?)null;
                var temp = metrics.Temperature > 0 ? metrics.Temperature : (double?)null;
                var uptime = metrics.Uptime > 0 ? metrics.Uptime / 100 : (long?)null;

                if (cpu != null || memPct != null)
                    snmpHealthHits[NormalizeMac(device.Mac)] = true;
                if (temp != null)
                    snmpTempHits[NormalizeMac(device.Mac)] = true;

                await _influx.WriteDeviceHealthAsync(
                    deviceMac: device.Mac,
                    deviceType: DescribeDeviceType(device.DeviceType),
                    cpuPercent: cpu,
                    memoryTotalKb: metrics.TotalMemory > 0 ? metrics.TotalMemory / 1024 : null,
                    memoryUsedKb: metrics.UsedMemory > 0 ? metrics.UsedMemory / 1024 : null,
                    memoryUsedPercent: memPct,
                    temperatureC: temp,
                    uptimeSeconds: uptime,
                    timestamp: DateTime.UtcNow);

                _liveStats.RecordHealth(device.Mac, cpu, memPct, temp, uptime, DateTime.UtcNow);

                if (customOids.TryGetValue(NormalizeMac(device.Mac), out var deviceCustomOids))
                    await PollCustomOidsAsync(poller, device.Mac, DescribeDeviceType(device.DeviceType), ip, deviceCustomOids, ct);

                await _deviceHealthAlertEvaluator.EvaluateAsync(
                    device.Mac, device.Name, DescribeDeviceType(device.DeviceType),
                    cpu, memPct,
                    temperatureC: temp,
                    tempHighThresholdC: ResolveTempThreshold(settings, device.DeviceType));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Medium-tier health poll failed for {Device}", device.Mac);
                NoteSnmpFailure(NormalizeMac(device.Mac));
            }
            finally { _snmpGate.Release(); }
        });
        await Task.WhenAll(deviceTasks);

        await CollectApiHealthFallbackAsync(settings, devices, snmpHealthHits, snmpTempHits,
            snmpHandledElsewhere: false);
    }

    /// <summary>
    /// UniFi API health pass: supplements or replaces SNMP health data.
    /// Non-SNMP devices: full fallback (CPU, mem, temp, uptime).
    /// SNMP devices: fill in gaps only (e.g., temperature on switches where
    /// SNMP doesn't report temp but UniFi API does).
    /// With <paramref name="snmpHandledElsewhere"/> (site has a connected agent
    /// relaying SNMP), SNMP-enabled devices are skipped entirely - the relay
    /// writes their health - and only devices the agent can't poll get API data.
    /// </summary>
    private async Task CollectApiHealthFallbackAsync(
        MonitoringSettings settings,
        List<UniFiDeviceResponse> devices,
        ConcurrentDictionary<string, bool> snmpHealthHits,
        ConcurrentDictionary<string, bool> snmpTempHits,
        bool snmpHandledElsewhere)
    {
        var now = DateTime.UtcNow;
        foreach (var device in devices)
        {
            var mac = NormalizeMac(device.Mac);
            var snmpOn = Monitoring.SnmpDeviceRules.HasSnmpEnabled(device);
            if (snmpHandledElsewhere && snmpOn) continue;
            var snmpExcl = IsSnmpExcluded(mac);
            var snmpActive = snmpOn && !snmpExcl;

            try
            {
                var ss = device.SystemStatsSimple;
                if (ss == null && device.Temperatures == null) continue;

                double? cpu = ss != null ? ParseJsonDouble(ss.Cpu) : null;
                double? mem = ss != null ? ParseJsonDouble(ss.Mem) : null;
                long? uptime = ss != null ? (long?)ParseJsonDouble(ss.Uptime) : null;
                double? temp = ParseDeviceTemperature(device);

                // SNMP devices that actually returned health data: only supplement
                // temp for switches and gateways. Some gateways (e.g., the UDM family)
                // return CPU/mem over SNMP but not temperature; the UniFi API exposes
                // it, so fill that gap. This is keyed off whether SNMP actually returned
                // a temperature at runtime (snmpTempHits), not off any model, so gateways
                // that do report temp over SNMP are untouched. Devices where SNMP is
                // configured but returned no health OIDs (e.g., USW-Flex-XG) fall through
                // to full API data.
                if (snmpActive && snmpHealthHits.ContainsKey(mac))
                {
                    if (device.DeviceType != NetworkOptimizer.Core.Enums.DeviceType.Switch
                        && device.DeviceType != NetworkOptimizer.Core.Enums.DeviceType.Gateway) continue;
                    cpu = null;
                    mem = null;
                    uptime = null;
                    // SNMP already reported a temperature for this device; don't
                    // double-write it from the API (avoids conflicting data points).
                    if (snmpTempHits.ContainsKey(mac)) continue;
                    if (temp == null) continue;
                }

                if (cpu == null && mem == null && temp == null) continue;

                await _influx.WriteDeviceHealthAsync(
                    deviceMac: device.Mac,
                    deviceType: DescribeDeviceType(device.DeviceType),
                    cpuPercent: cpu,
                    memoryTotalKb: null,
                    memoryUsedKb: null,
                    memoryUsedPercent: mem,
                    temperatureC: temp,
                    uptimeSeconds: uptime,
                    timestamp: now);

                _liveStats.RecordHealth(device.Mac, cpu, mem, temp, uptime, now);

                await _deviceHealthAlertEvaluator.EvaluateAsync(
                    device.Mac, device.Name, DescribeDeviceType(device.DeviceType),
                    cpu, mem,
                    temperatureC: temp,
                    tempHighThresholdC: ResolveTempThreshold(settings, device.DeviceType));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UniFi API health fallback failed for {Device}", device.Mac);
            }
        }
    }

    private static double? ParseJsonDouble(System.Text.Json.JsonElement? el) =>
        UniFiDeviceHealthReader.ParseJsonDouble(el);

    private static double? ParseDeviceTemperature(UniFiDeviceResponse device) =>
        UniFiDeviceHealthReader.ParseDeviceTemperature(device);

    private async Task SlowTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        // The SFP collection below reads UniFi port_table DDM values, so it works over
        // any reachable console connection (tunnel-proxied included). The SNMP interface
        // walk further down needs direct SNMP reach, which a connected agent already
        // provides from inside the site - skip the local copy in that case.
        var agentCovers = AgentCoversCollection();

        // Network config (cached ~5 min) feeds the WireGuard / OpenVPN / honeypot /
        // bridge interface labels resolved below. Best-effort: a fetch failure just
        // means those families fall back to their raw ifname.
        IReadOnlyList<NetworkInfo> networkConfigs = Array.Empty<NetworkInfo>();
        if (!agentCovers)
        {
            try { networkConfigs = await _connectionService.GetNetworksAsync(ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "networkconf fetch for interface labels failed"); }
        }

        var poller = agentCovers ? null : GetOrBuildPoller(settings);
        if (poller == null && !agentCovers) return;

        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);

        // Reconcile InterfaceNameMap: stable device_mac+ifName → friendly name from UniFi
        // (per spec 3.7).
        await using var db = await CreateSiteDbAsync(ct);
        var existingMaps = await db.InterfaceNameMaps.ToDictionaryAsync(
            m => (m.DeviceMac, m.IfName), m => m, ct);
        var existingSfps = await db.MonitoredSfps.ToDictionaryAsync(
            s => (s.DeviceMac, s.PortName), s => s, ct);

        // SFP collection (spec 5.9): write DDM values to the sfp measurement for every port
        // where UniFi reports sfp_found, and reconcile the MonitoredSfps relational row.
        var nowSfp = DateTime.UtcNow;
        foreach (var device in devices)
        {
            CollectSfpForDevice(device, db, existingSfps, nowSfp);
        }

        // SFP threshold evaluation: check DDM values against alert thresholds
        // (per-category, user-configurable with built-in fallbacks).
        var sfpThresholds = NetworkOptimizer.Web.Services.Monitoring.SfpDdmThresholds.FromSettings(settings);
        foreach (var device in devices)
        {
            if (device.PortTable == null || device.PortTable.Count == 0) continue;
            var sfpMac = NormalizeMac(device.Mac);
            foreach (var port in device.PortTable)
            {
                if (port.SfpFound != true) continue;
                var sfpPortName = port.PortIdx > 0
                    ? port.PortIdx.ToString()
                    : (port.Name ?? string.Empty);
                if (string.IsNullOrEmpty(sfpPortName)) continue;
                var sfpCategory = existingSfps.TryGetValue((sfpMac, sfpPortName), out var sfpRow)
                    ? sfpRow.Category : SfpCategory.Standard;
                try
                {
                    await _sfpAlertEvaluator.EvaluateAsync(
                        sfpMac, sfpPortName, device.Name, sfpCategory,
                        port.SfpRxPower, port.SfpTxPower, port.SfpTemperature, sfpThresholds, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SFP alert evaluation failed for {Mac} port {Port}", sfpMac, sfpPortName);
                }
            }
        }

        var gatewayLanIp = agentCovers ? null : await ResolveGatewayLanIpAsync(ct);
        foreach (var device in agentCovers ? Enumerable.Empty<UniFiDeviceResponse>() : devices)
        {
            if (!Monitoring.SnmpDeviceRules.HasSnmpEnabled(device))
                continue;
            if (IsSnmpExcluded(NormalizeMac(device.Mac))) continue;
            try
            {
                var pollIp = ResolveSnmpAddress(device, gatewayLanIp);
                if (!IPAddress.TryParse(pollIp, out var ip)) continue;
                var interfaces = await poller!.GetInterfaceMetricsAsync(ip, device.Name);
                if (interfaces.Count == 0)
                {
                    NoteSnmpFailure(NormalizeMac(device.Mac));
                    continue;
                }
                var deviceIfNames = new List<string>();
                foreach (var iface in interfaces)
                {
                    var ifName = string.IsNullOrEmpty(iface.Name) ? iface.Description : iface.Name;
                    if (string.IsNullOrEmpty(ifName)) continue;
                    deviceIfNames.Add(ifName);
                    var key = (NormalizeMac(device.Mac), ifName);

                    // Map SNMP ifIndex to UniFi PortTable.PortIdx via the shared
                    // correlation helper (switches by ifIndex == PortIdx, gateways by
                    // PortTable.IfName == the raw SNMP ifName; link speed is "lower of
                    // the two wins" - see InterfacePortCorrelation). The matched port
                    // supplies BOTH the port number and the UniFi friendly name, so
                    // gateways (which only resolve via the IfName join) get a friendly
                    // name too, not just switches.
                    var corr = InterfacePortCorrelation.Correlate(
                        device.PortTable,
                        iface.Index,
                        iface.HighSpeed > 0 ? iface.HighSpeed * 1_000_000 : iface.Speed,
                        iface.PortId,
                        ifName);
                    int? portNumber = corr.PortNumber;
                    var friendlyName = corr.FriendlyName;
                    var isSfp = corr.IsSfp;
                    int? linkSpeedMbps = corr.LinkSpeedMbps;

                    if (!existingMaps.TryGetValue(key, out var mapping))
                    {
                        mapping = new InterfaceNameMap
                        {
                            DeviceMac = key.Item1,
                            IfName = ifName,
                            IfIndex = iface.Index,
                            IfAlias = iface.Description,
                            SpeedMbps = linkSpeedMbps,
                            FriendlyName = friendlyName,
                            PortNumber = portNumber,
                            IsSfp = isSfp,
                            LastUpdated = DateTime.UtcNow
                        };
                        db.InterfaceNameMaps.Add(mapping);
                        // Register so duplicates within the same cycle (e.g. an interface
                        // surfacing twice from a Walk) hit the update branch instead of
                        // re-inserting and tripping the UNIQUE(DeviceMac, IfName) constraint.
                        existingMaps[key] = mapping;
                    }
                    else
                    {
                        mapping.IfIndex = iface.Index;
                        mapping.IfAlias = iface.Description;
                        if (linkSpeedMbps.HasValue) mapping.SpeedMbps = linkSpeedMbps;
                        if (!string.IsNullOrEmpty(friendlyName)) mapping.FriendlyName = friendlyName;
                        if (portNumber.HasValue)
                        {
                            mapping.PortNumber = portNumber;
                        }
                        else if (mapping.PortNumber is int stale
                            && InterfacePortCorrelation.PortNumberBelongsToOtherInterface(device.PortTable, mapping.IfName, stale, iface.PortId))
                        {
                            // Heal rows written before the numeric ifIndex match was gated
                            // to entries without an ifname: the stored number (and the
                            // friendly name / SFP flag copied with it) belongs to the
                            // interface the port_table entry names, not this one.
                            mapping.PortNumber = null;
                            mapping.FriendlyName = null;
                            mapping.IsSfp = null;
                        }
                        if (isSfp.HasValue) mapping.IsSfp = isSfp;
                        mapping.LastUpdated = DateTime.UtcNow;
                    }
                }

                // Heal rows for interfaces this walk no longer returns (a gateway's
                // dummy0 / ip_vti0 / bond0 era): a stored port number that provably
                // belongs to another interface was written before the numeric ifIndex
                // match was gated to entries without an ifname, and with the interface
                // gone from the walk the update branch above never revisits the row -
                // the false claim (and the name/SFP flag copied with it) would stick
                // forever. Rows whose claim the port table doesn't contradict are
                // never touched. No rawIfName here BY DESIGN: unwalked rows have no
                // current sample to supply one, so an alias-keyed row whose interface
                // has left the walk can be cleared - accepted, since a departed
                // interface's port claim is stale anyway.
                var walkedNames = new HashSet<string>(deviceIfNames, StringComparer.OrdinalIgnoreCase);
                var deviceMacNorm = NormalizeMac(device.Mac);
                foreach (var ((rowMac, rowIfName), row) in existingMaps)
                {
                    if (rowMac != deviceMacNorm || walkedNames.Contains(rowIfName)) continue;
                    if (row.PortNumber is int staleClaim
                        && InterfacePortCorrelation.PortNumberBelongsToOtherInterface(device.PortTable, rowIfName, staleClaim))
                    {
                        row.PortNumber = null;
                        row.FriendlyName = null;
                        row.IsSfp = null;
                        row.LastUpdated = DateTime.UtcNow;
                    }
                }

                // Resolve friendly interface labels (WANn - carrier, WireGuard, SQM,
                // honeypot, ...) from the device config + networkconf and cache them for
                // the Live View port table.
                //
                // FUTURE TIME SERIES TOUCH POINT: this is where per-interface identity
                // (label, WAN group, carrier, media) and status (oper_status) would also
                // be written to InfluxDB so the port table can play back historical
                // identity/status, not just the current snapshot. Resolved here, agent-
                // side, precisely so that move is a localized addition.
                _liveStats.RecordInterfaceLabels(NormalizeMac(device.Mac),
                    InterfaceLabelResolver.BuildLabels(device, networkConfigs, deviceIfNames));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Slow-tier metadata poll failed for {Device}", device.Mac);
                NoteSnmpFailure(NormalizeMac(device.Mac));
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ---- WiFi client tier (spec 5.2) ----
    //
    // TODO (post-MVP): if the 30-second stat/sta cadence isn't realtime enough for the
    // live map's WiFi client leaf nodes, layer the WiFiman per-client endpoint on top
    // (GET /v2/api/site/{site}/wifiman/{clientIp}/) for the *currently-hovered* or
    // *currently-selected* client only. WiFiman gives sub-second signal / experience /
    // neighbor data but is per-client - hitting it for every WiFi client on every map
    // tick would crush the controller. The existing Client Dashboard already uses
    // WiFiman for its deep-dive view; reuse that pattern (selected client only) if/when
    // we need it for the 3D map. Until then, stat/sta at 30s is plenty for the snapshot
    // collection use case.

    /// <summary>
    /// Per-client byte counter cache for delta-derived throughput rates. Same approach
    /// as SNMP interface counter rate computation: store prev sample, diff against
    /// current, compute bps. UniFi's tx_bytes-r / rx_bytes-r fields are preferred when
    /// the API returns them (active clients only); we fall back to this cache for idle
    /// clients with stale -r fields.
    /// </summary>
    private readonly ConcurrentDictionary<string, ClientByteSnapshot> _wifiByteCache = new();
    private readonly ConcurrentDictionary<string, ClientByteSnapshot> _wiredByteCache = new();
    private readonly record struct ClientByteSnapshot(DateTime Timestamp, long TxBytes, long RxBytes);

    private async Task WifiClientTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null) return;
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);

        UniFiClientResponse[] clients;
        try
        {
            var raw = await _connectionService.Client.GetClientsAsync(ct);
            clients = raw?.ToArray() ?? Array.Empty<UniFiClientResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WiFi tier: GetClientsAsync failed");
            return;
        }

        var now = DateTime.UtcNow;
        var wifiCount = clients.Count(c => !c.IsWired);
        var wiredCount = clients.Count(c => c.IsWired);
        _logger.LogDebug("WiFi tier: {Total} clients ({Wifi} wifi, {Wired} wired)", clients.Length, wifiCount, wiredCount);

        // Map each switch/gateway port that has exactly one wired client to that client,
        // for the Live View port stats "Client" column. Ports with multiple MACs
        // (uplinks/trunks) are skipped so we never label them with an arbitrary client.
        var wiredByPort = new Dictionary<(string, int), List<UniFiClientResponse>>();
        foreach (var c in clients)
        {
            if (!c.IsWired || string.IsNullOrEmpty(c.Mac) || string.IsNullOrEmpty(c.SwMac)
                || c.SwPort is not int swp || swp <= 0) continue;
            var key = (NormalizeMac(c.SwMac), swp);
            if (!wiredByPort.TryGetValue(key, out var list)) { list = new(); wiredByPort[key] = list; }
            list.Add(c);
        }
        var portClients = new Dictionary<(string DeviceMac, int Port), MonitoringLiveStats.PortClient>();
        foreach (var (key, list) in wiredByPort)
        {
            if (list.Count != 1) continue;
            var pc = list[0];
            var pcName = !string.IsNullOrWhiteSpace(pc.Name) ? pc.Name
                : !string.IsNullOrWhiteSpace(pc.Hostname) ? pc.Hostname : pc.Mac;
            // BestIp falls back ip -> last_ip -> fixed_ip, so fixed/reservation devices
            // (no live DHCP lease) still resolve, matching the UniFi client table.
            portClients[key] = new MonitoringLiveStats.PortClient(pc.Mac, pc.BestIp ?? string.Empty, pcName);
        }
        _liveStats.RecordPortClients(portClients);

        long tickOffset = 0; // nanosecond offset per client to avoid InfluxDB dedup
        foreach (var c in clients)
        {
            if (c.IsWired) continue;
            if (string.IsNullOrEmpty(c.Mac)) continue;
            var band = MapBand(c.Radio);
            if (string.IsNullOrEmpty(band)) continue; // only WiFi clients with a known band

            var apMac = NormalizeMac(c.ApMac ?? string.Empty);
            var clientMac = NormalizeMac(c.Mac);

            // Throughput: prefer UniFi's rolling per-second fields when populated, else
            // compute from the cumulative byte counters' delta vs previous snapshot.
            double? txThroughputBps = null;
            double? rxThroughputBps = null;
            if (c.TxBytesRate > 0 || c.RxBytesRate > 0)
            {
                txThroughputBps = c.TxBytesRate * 8.0;
                rxThroughputBps = c.RxBytesRate * 8.0;
                _wifiByteCache[clientMac] = new ClientByteSnapshot(now, c.TxBytes, c.RxBytes);
            }
            else if (_wifiByteCache.TryGetValue(clientMac, out var prev))
            {
                long deltaTx = c.TxBytes - prev.TxBytes;
                long deltaRx = c.RxBytes - prev.RxBytes;
                if (deltaTx > 0 || deltaRx > 0)
                {
                    var elapsed = (now - prev.Timestamp).TotalSeconds;
                    if (elapsed > 0.5)
                    {
                        txThroughputBps = deltaTx * 8.0 / elapsed;
                        rxThroughputBps = deltaRx * 8.0 / elapsed;
                    }
                    _wifiByteCache[clientMac] = new ClientByteSnapshot(now, c.TxBytes, c.RxBytes);
                }
            }
            else
            {
                _wifiByteCache[clientMac] = new ClientByteSnapshot(now, c.TxBytes, c.RxBytes);
            }

            var snapshot = new WifiClientLiveSnapshot
            {
                ClientMac = clientMac,
                ApMac = apMac,
                Band = band,
                Channel = c.Channel,
                ChannelWidth = c.ChannelWidth,
                SignalDbm = c.Signal,
                NoiseDbm = c.Noise,
                TxRateKbps = c.TxRate > 0 ? c.TxRate : null,
                RxRateKbps = c.RxRate > 0 ? c.RxRate : null,
                TxThroughputBps = txThroughputBps,
                RxThroughputBps = rxThroughputBps,
                Satisfaction = c.Satisfaction,
                Rssi = c.Rssi,
                IsMlo = c.IsMlo ?? false,
                Hostname = string.IsNullOrEmpty(c.Name) ? (string.IsNullOrEmpty(c.Hostname) ? null : c.Hostname) : c.Name,
                LastUpdate = now
            };
            _liveStats.RecordWifiClient(snapshot);

            if ((txThroughputBps ?? 0) > 0 || (rxThroughputBps ?? 0) > 0)
            {
                _ = _influx.WriteWifiClientAsync(
                    apMac: apMac,
                    band: band,
                    clientMac: clientMac,
                    signalDbm: c.Signal,
                    noiseDbm: c.Noise,
                    txRateKbps: c.TxRate > 0 ? c.TxRate : null,
                    rxRateKbps: c.RxRate > 0 ? c.RxRate : null,
                    channel: c.Channel,
                    channelWidth: c.ChannelWidth,
                    satisfaction: c.Satisfaction,
                    rssi: c.Rssi,
                    txBytes: c.TxBytes,
                    rxBytes: c.RxBytes,
                    txThroughputBps: txThroughputBps,
                    rxThroughputBps: rxThroughputBps,
                    isMlo: c.IsMlo,
                    timestamp: now.AddTicks(tickOffset++));
            }
        }

        // Wired clients: collect throughput as fallback for non-SNMP switches.
        // Uses the same tx_bytes/rx_bytes delta approach as WiFi clients.
        foreach (var c in clients)
        {
            if (!c.IsWired) continue;
            if (string.IsNullOrEmpty(c.Mac)) continue;
            var clientMac = NormalizeMac(c.Mac);

            double? txBps = null, rxBps = null;
            // Wired clients use wired-tx_bytes-r / wired-rx_bytes-r (not tx_bytes-r)
            if (c.WiredTxBytesRate > 0 || c.WiredRxBytesRate > 0)
            {
                txBps = c.WiredTxBytesRate * 8.0;
                rxBps = c.WiredRxBytesRate * 8.0;
                _wiredByteCache[clientMac] = new ClientByteSnapshot(now, c.WiredTxBytes, c.WiredRxBytes);
            }
            else if (_wiredByteCache.TryGetValue(clientMac, out var prev))
            {
                long deltaTx = c.WiredTxBytes - prev.TxBytes;
                long deltaRx = c.WiredRxBytes - prev.RxBytes;
                if (deltaTx > 0 || deltaRx > 0)
                {
                    // Only compute rate when counters actually changed. Elapsed is
                    // time since last CHANGE, not last poll - avoids 2x rate when
                    // our poll misaligns with UniFi's counter update cadence.
                    var elapsed = (now - prev.Timestamp).TotalSeconds;
                    if (elapsed > 0.5)
                    {
                        txBps = deltaTx * 8.0 / elapsed;
                        rxBps = deltaRx * 8.0 / elapsed;
                    }
                    _wiredByteCache[clientMac] = new ClientByteSnapshot(now, c.WiredTxBytes, c.WiredRxBytes);
                }
                // When counters unchanged, DON'T update cache timestamp - preserves
                // the real elapsed time for the next actual change.
            }
            else
            {
                // First poll: seed cache, no rate yet
                _wiredByteCache[clientMac] = new ClientByteSnapshot(now, c.WiredTxBytes, c.WiredRxBytes);
            }

            _liveStats.RecordWiredClient(new WiredClientLiveSnapshot
            {
                ClientMac = clientMac,
                TxThroughputBps = txBps,
                RxThroughputBps = rxBps,
                LastUpdate = now,
            });

            // Write to InfluxDB for historic playback. Active clients always write;
            // idle (zero-throughput) clients write only when they carry a switch-port
            // association - the port-tagged presence is what lets playback show the
            // Client column for a port even while the client is quiet.
            var swMac = NormalizeMac(c.SwMac ?? string.Empty);
            var swPort = c.SwPort is int sp && sp > 0 ? sp : (int?)null;
            if (!string.IsNullOrEmpty(swMac) && ((txBps ?? 0) > 0 || (rxBps ?? 0) > 0 || swPort.HasValue))
            {
                var displayName = !string.IsNullOrWhiteSpace(c.Name) ? c.Name
                    : !string.IsNullOrWhiteSpace(c.Hostname) ? c.Hostname : c.Mac;
                _ = _influx.WriteWiredClientAsync(
                    switchMac: swMac,
                    clientMac: clientMac,
                    txThroughputBps: txBps,
                    rxThroughputBps: rxBps,
                    timestamp: now.AddTicks(tickOffset++),
                    port: swPort,
                    clientIp: c.BestIp,
                    clientName: displayName);
            }
        }

        // Drop stale byte-cache entries for clients we haven't seen this cycle. Otherwise
        // a roamed/disconnected client's stale counter sits forever and gives a bogus
        // delta on reconnect.
        var seenWifi = new HashSet<string>(clients.Where(c => !c.IsWired).Select(c => NormalizeMac(c.Mac)));
        foreach (var key in _wifiByteCache.Keys)
            if (!seenWifi.Contains(key)) _wifiByteCache.TryRemove(key, out _);

        var seenWired = new HashSet<string>(clients.Where(c => c.IsWired).Select(c => NormalizeMac(c.Mac)));
        foreach (var key in _wiredByteCache.Keys)
            if (!seenWired.Contains(key)) _wiredByteCache.TryRemove(key, out _);
    }

    /// <summary>
    /// Normalize UniFi's `radio` field into a "Xghz" band string. UniFi uses "ng" for
    /// 2.4 GHz, "na" for 5 GHz, "6e" / "6g" for 6 GHz. We use these exact strings as
    /// the InfluxDB tag value to keep the wifi_client measurement's cardinality on
    /// 3 distinct values per AP.
    /// </summary>
    private static string MapBand(string? radio) => radio switch
    {
        "ng" => "2.4ghz",
        "na" => "5ghz",
        "6e" => "6ghz",
        "6g" => "6ghz",
        _ => string.Empty
    };

    // ---- Health revalidation tier ----

    /// <summary>
    /// Background InfluxDB health probe. CheckHealthAsync now hits the bucket via a
    /// Flux query (not just /ping), so this catches the case where the user revokes
    /// the token or deletes the buckets without having to click the Test button on
    /// the dashboard. Results are persisted to MonitoringSettings for the UI to read.
    /// </summary>
    private async Task HealthTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        try
        {
            await _influx.CheckHealthAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background health probe failed");
        }
    }

    // ---- Latency / loss tier ----

    private async Task LatencyTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);

        // Reconcile fabric targets with the live device list — gateways, switches, and APs
        // should each have a `fabric` target on their management IP (spec 5.4). New devices
        // add a target; deleted devices leave their targets untouched (history preserved).
        // This runs even when an agent covers the site: the reconciled targets are what
        // the tunnel pushes to the agent for probing from inside the network.
        await ReconcileFabricTargetsAsync(ct);

        // Probing itself stands down for every non-default (external) site, agent or not. The
        // central server can't see the site's network: with an agent connected it double-probes
        // the WAN path instead of the site's own fabric/WAN view, and with no agent yet it would
        // log its own anycast RTT as the site's ISP latency. The site's agent probes its enabled
        // targets from inside once deployed (AgentProbeResultSink). The default site keeps
        // probing locally as before.
        if (!_isDefault) return;

        await using var db = await CreateSiteDbAsync(ct);
        var targets = await db.MonitoringTargets
            .AsNoTracking()
            .Where(t => t.Enabled)
            .ToListAsync(ct);
        var contextsById = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);

        var now = DateTime.UtcNow;
        var dueTargets = new List<(MonitoringTarget Target, WanContext? WanContext)>();
        foreach (var target in targets.Where(t => IsDue(t, now)))
        {
            var context = target.WanContextId is int contextId && contextsById.TryGetValue(contextId, out var c)
                ? c : null;
            // Targets in an agent-assigned WAN context are probed by that agent
            // (which sits behind the right WAN); probing them from here would
            // measure the wrong path.
            if (context?.AgentId != null) continue;
            dueTargets.Add((target, context));
        }
        if (dueTargets.Count == 0) return;

        // Probe in parallel but bounded so we don't fan out to dozens of pings at once on a
        // dense fabric. 8 concurrent is well under what the local executor can handle.
        using var concurrency = new SemaphoreSlim(8);
        var tasks = dueTargets.Select(x => ProbeTargetAsync(x.Target, x.WanContext, concurrency, ct));
        await Task.WhenAll(tasks);
    }

    private bool IsDue(MonitoringTarget target, DateTime now)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, target.PollIntervalSeconds));
        if (!_targetLastProbed.TryGetValue(target.Id, out var last)) return true;
        return now - last >= interval;
    }

    private async Task ProbeTargetAsync(MonitoringTarget target, WanContext? wanContext, SemaphoreSlim concurrency, CancellationToken ct)
    {
        await concurrency.WaitAsync(ct);
        try
        {
            _targetLastProbed[target.Id] = DateTime.UtcNow;

            // A WAN context's probe source IP rides into the probe (ping -I / TCP
            // socket bind); the gateway policy-routes that source out the WAN
            // being measured.
            var probeTarget = new ProbeTarget(target.Address, target.ProbeMode, target.Port, wanContext?.ProbeSourceIp);
            var vantage = string.IsNullOrEmpty(target.VantagePoint) ? "server" : target.VantagePoint;
            // MVP: only server-side probes. Per-device SSH vantages come with the SSH
            // toolbox device picker (planned next).
            if (!string.Equals(vantage, "server", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping target {Target} — SSH vantage {Vantage} not wired yet",
                    target.TargetId, vantage);
                return;
            }

            var ping = await _localProbe.PingAsync(probeTarget, count: Math.Max(3, Math.Min(target.PingCount, 20)), perPingTimeout: TimeSpan.FromSeconds(2), ct: ct);

            if (ct.IsCancellationRequested) return;

            await _influx.WriteLatencyAsync(
                targetId: target.TargetId,
                vantagePoint: vantage,
                targetType: target.TargetType,
                probeMode: target.ProbeMode,
                rttMinMs: ping.RttMinMs,
                rttAvgMs: ping.RttAvgMs,
                rttMaxMs: ping.RttMaxMs,
                jitterMs: ping.JitterMs,
                lossPercent: ping.LossPercent,
                success: ping.Success,
                sent: ping.Sent,
                received: ping.Received,
                timestamp: ping.Timestamp,
                wanContext: wanContext?.Name);

            // Surface fabric probe results on the dashboard's device cards (5.6). Other
            // target types (WAN, transit) feed cloud nodes on the 3D map; the per-device
            // card only cares about its own fabric probe.
            if (target.TargetType == MonitoringTargetType.Fabric && !string.IsNullOrEmpty(target.DeviceMac))
            {
                _liveStats.RecordLatency(target.DeviceMac, ping.RttAvgMs, ping.LossPercent, ping.Timestamp);
            }
            // Always record per-target stats so the targets table can show latest results
            // regardless of target type.
            _liveStats.RecordTargetProbe(target.TargetId, ping.RttAvgMs, ping.LossPercent, ping.Success, ping.Timestamp);

            // State-change evaluation: publish AlertEvents on up→down, down→up, and
            // sustained packet loss transitions. Cheap, in-memory state machine.
            try { await _alertEvaluator.EvaluateAsync(target, ping, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Alert evaluator failed for target {Target}", target.TargetId); }

            if (ping.Success)
            {
                // Persist last-verified time on success — used by both UI ("last seen") and
                // the wizard's re-validation (spec 5.5).
                try
                {
                    await using var db = await CreateSiteDbAsync(ct);
                    var row = await db.MonitoringTargets.FindAsync(new object[] { target.Id }, ct);
                    if (row != null)
                    {
                        row.LastVerified = ping.Timestamp;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to persist LastVerified for {Target}", target.TargetId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Latency probe failed for {Target}", target.TargetId);
        }
        finally
        {
            concurrency.Release();
        }
    }

    // TargetIds of the two default internet targets created by SeedDefaultTargetsAsync. Kept in
    // sync with the literals in the seed rows below; used by EnableSeededDefaultTargetsAsync.
    private static readonly string[] DefaultInternetTargetIds = { "wan-cloudflare-1111", "wan-google-8888" };

    private async Task SeedDefaultTargetsAsync(CancellationToken ct)
    {
        await using var db = await CreateSiteDbAsync(ct);
        var existing = await db.MonitoringTargets.AsNoTracking()
            .Select(t => t.TargetId).ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var defaults = new[]
        {
            new MonitoringTarget
            {
                TargetId = "wan-cloudflare-1111",
                Name = "Cloudflare (1.1.1.1)",
                Address = "1.1.1.1",
                ProbeMode = ProbeMode.Icmp,
                TargetType = MonitoringTargetType.InternetService,
                VantagePoint = "server",
                PollIntervalSeconds = 10,
                PingCount = 5,
                // External sites seed disabled until an agent is deployed (enabled inline on the
                // default site and on external sites that already have an agent). See
                // EnableSeededDefaultTargetsAsync for the enroll-time flip.
                Enabled = _isDefault || _siteAgentEnrolled,
                AutoDiscovered = true,
                AutoLabel = "Cloudflare DNS",
                CreatedAt = DateTime.UtcNow
            },
            new MonitoringTarget
            {
                TargetId = "wan-google-8888",
                Name = "Google (8.8.8.8)",
                Address = "8.8.8.8",
                ProbeMode = ProbeMode.Icmp,
                TargetType = MonitoringTargetType.InternetService,
                VantagePoint = "server",
                PollIntervalSeconds = 10,
                PingCount = 5,
                // See the Cloudflare row above: disabled on an external site until an agent exists.
                Enabled = _isDefault || _siteAgentEnrolled,
                AutoDiscovered = true,
                AutoLabel = "Google DNS",
                CreatedAt = DateTime.UtcNow
            }
        };

        bool added = false;
        foreach (var t in defaults)
        {
            if (!existingSet.Contains(t.TargetId))
            {
                db.MonitoringTargets.Add(t);
                added = true;
            }
        }
        if (added) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Activate the two seeded default internet targets once an agent is deployed for a
    /// (non-default) external site. They are seeded disabled while the site has no agent so the
    /// central server never probes anycast DNS from its own stack and mislabels it as the site's
    /// ISP latency; once an agent can probe them from inside the network, baseline internet
    /// monitoring should begin. Only ever touches a default that is still disabled AND has never
    /// been probed (LastVerified == null): the enroll "transition" re-fires on every process
    /// restart (the flag is in-memory), and that never-verified guard keeps it from re-enabling a
    /// default the user has since paused. Default site never calls this (RefreshAgentCoverageAsync
    /// returns early there).
    /// </summary>
    private async Task EnableSeededDefaultTargetsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            var rows = await db.MonitoringTargets
                .Where(t => DefaultInternetTargetIds.Contains(t.TargetId) && !t.Enabled && t.LastVerified == null)
                .ToListAsync(ct);
            if (rows.Count == 0) return;
            foreach (var t in rows) t.Enabled = true;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Enabled {Count} seeded default internet target(s) for site {Slug} after agent enrollment",
                rows.Count, _siteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Enabling seeded default targets on enrollment failed for site {Slug}", _siteSlug);
        }
    }

    private async Task ReconcileFabricTargetsAsync(CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0)
        {
            _logger.LogDebug(
                "Fabric reconcile for site {Slug}: no monitorable devices to seed targets from (console connected: {Connected})",
                _siteSlug, _connectionService.IsConnected);
            return;
        }

        // Gateways need the LAN-side address here for the same reason SNMP does:
        // UniFi's "ip" field is the WAN public IP, which often doesn't answer ping
        // from inside the LAN (PPPoE, CGNAT, upstream-assigned). The Address refresh
        // below also migrates existing targets seeded with the WAN IP.
        var gatewayLanIp = await ResolveGatewayLanIpAsync(ct);

        await using var db = await CreateSiteDbAsync(ct);
        var existingByTargetId = await db.MonitoringTargets
            .Where(t => t.TargetType == MonitoringTargetType.Fabric)
            .ToDictionaryAsync(t => t.TargetId, ct);

        var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
        var needsFlex25GMigration = settings != null && !settings.Flex25GLatencyMigrated;
        var deviceModelByMac = needsFlex25GMigration
            ? devices.Where(d => !string.IsNullOrEmpty(d.Mac))
                .ToDictionary(d => NormalizeMac(d.Mac), d => d, StringComparer.OrdinalIgnoreCase)
            : null;

        if (needsFlex25GMigration)
        {
            int disabled = 0;
            foreach (var existing in existingByTargetId.Values)
            {
                if (!existing.AutoDiscovered || !existing.Enabled) continue;
                if (string.IsNullOrEmpty(existing.DeviceMac)) continue;
                if (deviceModelByMac!.TryGetValue(existing.DeviceMac, out var dev)
                    && UniFi.UniFiProductDatabase.IsFlex25G(dev.Model, dev.Shortname))
                {
                    existing.Enabled = false;
                    disabled++;
                    _logger.LogInformation("Disabled latency probing for Flex 2.5G target {Name} ({Mac})",
                        existing.Name, existing.DeviceMac);
                }
            }
            settings!.Flex25GLatencyMigrated = true;
            _logger.LogInformation("Flex 2.5G latency migration complete, disabled {Count} target(s)", disabled);
        }

        bool changed = false;
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Ip) || string.IsNullOrEmpty(d.Mac)) continue;
            var address = ResolveSnmpAddress(d, gatewayLanIp);
            var targetId = $"fabric-{NormalizeMac(d.Mac)}";
            if (existingByTargetId.TryGetValue(targetId, out var existing))
            {
                // Refresh address in case the device's management IP changed.
                if (existing.Address != address)
                {
                    existing.Address = address;
                    changed = true;
                }
                // Flex 2.5G switches are poor latency/loss targets (high RTT/jitter),
                // so they're disabled by default. The one-shot Flex25GLatencyMigrated
                // pass can miss an existing target if the device's model hadn't resolved
                // yet at that instant (offline, or just after a controller reconnect).
                // Re-assert it here every reconcile so a missed target self-heals once
                // the model is known. Only writes when currently enabled, so steady
                // state is a no-op (no per-tick DB churn).
                if (existing.AutoDiscovered && existing.Enabled
                    && UniFi.UniFiProductDatabase.IsFlex25G(d.Model, d.Shortname))
                {
                    existing.Enabled = false;
                    changed = true;
                    _logger.LogInformation(
                        "Disabled latency probing for Flex 2.5G target {Name} ({Mac})",
                        existing.Name, existing.DeviceMac);
                }
                continue;
            }

            var enableLatency = !UniFi.UniFiProductDatabase.IsFlex25G(d.Model, d.Shortname);
            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = targetId,
                Name = string.IsNullOrEmpty(d.Name) ? d.Mac : d.Name,
                Address = address,
                ProbeMode = ProbeMode.Icmp,
                TargetType = MonitoringTargetType.Fabric,
                DeviceMac = NormalizeMac(d.Mac),
                VantagePoint = "server",
                PollIntervalSeconds = 5,
                PingCount = 3,
                Enabled = enableLatency,
                AutoDiscovered = true,
                AutoLabel = DescribeDeviceType(d.DeviceType),
                CreatedAt = DateTime.UtcNow
            });
            changed = true;
        }
        if (changed || needsFlex25GMigration) await db.SaveChangesAsync(ct);
    }

    // ---- Helpers ----

    private (double? RateInBps, double? RateOutBps) WriteInterfaceCounters(UniFiDeviceResponse device, InterfaceMetrics iface, DateTime now)
    {
        var ifName = iface.MonitoredName;
        if (string.IsNullOrEmpty(ifName)) return (null, null);
        var mac = NormalizeMac(device.Mac);

        // Compute rate from previous snapshot. The calculator handles 32-bit wrap,
        // unchanged-counter holds, genuine resets, and single-sample SNMP glitches
        // (which would otherwise inject impossible terabit/sec spikes or poison the
        // baseline - see InterfaceRateCalculator).
        var key = $"{mac}/{ifName}";
        bool hcCounters = iface.UsesHcCounters;
        long speedBps = iface.ResolvedSpeedBps;

        InterfaceRateCalculator.State? prevState =
            _counterCache.TryGetValue(key, out var cached) ? cached : null;
        var calc = InterfaceRateCalculator.Compute(
            prevState, iface.InOctets, iface.OutOctets, now, hcCounters, speedBps);
        _counterCache[key] = calc.NewState;

        double? rateInBps = calc.RateInBps;
        double? rateOutBps = calc.RateOutBps;

        switch (calc.Outcome)
        {
            case InterfaceRateCalculator.Outcome.ResetConfirmed:
                _logger.LogInformation(
                    "SNMP counter reset confirmed for {Mac}/{IfName}; reseeding baseline.",
                    mac, ifName);
                break;
            case InterfaceRateCalculator.Outcome.ImplausibleRate:
                _logger.LogWarning(
                    "Discarding implausible SNMP rate for {Mac}/{IfName}: in={RateIn:F0} out={RateOut:F0} bps exceed link speed {LinkBps} bps (x{Margin}). Likely a corrupt counter read.",
                    mac, ifName, calc.RejectedRateInBps ?? 0, calc.RejectedRateOutBps ?? 0, speedBps,
                    InterfaceRateCalculator.LinkSpeedToleranceFactor);
                break;
        }

        if (rateInBps.HasValue && rateOutBps.HasValue)
        {
            // Mirror into the read-side per-port cache so the 3D map's live tick
            // refreshes wired client leaf rates on the clean 5s SNMP cadence
            // (UniFi PortTable lags ~30s).
            // Direction: rateOutBps = port TX = data toward the leaf (DownBps in
            // cache convention); rateInBps = port RX = data from the leaf (UpBps).
            _liveStats.RecordPortRate(mac, ifName, rateOutBps.Value, rateInBps.Value, now);
        }

        _ = _influx.WriteInterfaceCountersAsync(
            deviceMac: mac,
            ifName: ifName,
            portId: iface.PortId,
            direction: InterfaceDirection.Unknown, // topology-driven direction set in a later build
            bytesIn: iface.InOctets,
            bytesOut: iface.OutOctets,
            rateInBps: rateInBps,
            rateOutBps: rateOutBps,
            speedBps: speedBps > 0 ? speedBps : null,
            operStatus: iface.OperStatus,
            errorsIn: iface.InErrors,
            errorsOut: iface.OutErrors,
            discardsIn: iface.InDiscards,
            discardsOut: iface.OutDiscards,
            hcCounters: hcCounters,
            ucastPktsIn: iface.InUcastPkts > 0 ? iface.InUcastPkts : null,
            ucastPktsOut: iface.OutUcastPkts > 0 ? iface.OutUcastPkts : null,
            mcastPktsIn: iface.InMulticastPkts > 0 ? iface.InMulticastPkts : null,
            mcastPktsOut: iface.OutMulticastPkts > 0 ? iface.OutMulticastPkts : null,
            bcastPktsIn: iface.InBroadcastPkts > 0 ? iface.InBroadcastPkts : null,
            bcastPktsOut: iface.OutBroadcastPkts > 0 ? iface.OutBroadcastPkts : null,
            timestamp: now);

        // Live port-state resilience for gateways: when UniFi's last poll says the port is
        // down or disabled, mark the live port down - UNLESS the SNMP frame counters moved
        // this poll, which proves it is passing traffic and UniFi's state is stale. Scoped to
        // the in-memory live cache; the InfluxDB write above keeps the raw SNMP ifOperStatus.
        // KEEP IN SYNC with the agent-relayed path in
        // AgentProbeResultSink.RecordSnmpBatchAsync ("Live port-state resilience for
        // gateways") - if you adjust one, adjust the other.
        int liveOperStatus = iface.OperStatus;
        if (device.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Gateway && !(rateInBps > 0) && !(rateOutBps > 0))
        {
            var uniPort = device.PortTable?.FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.IfName)
                && string.Equals(p.IfName, ifName, StringComparison.OrdinalIgnoreCase));
            if (uniPort != null && (!uniPort.Up || !uniPort.Enable))
                liveOperStatus = 2; // ifOperStatus down
        }

        // Mirror the full per-port snapshot into the live cache so the Live View port
        // stats table can serve live mode from memory instead of querying InfluxDB.
        _liveStats.RecordPortStats(new MonitoringInfluxClient.PortStatsPoint
        {
            DeviceMac = mac,
            IfName = ifName,
            PortId = iface.PortId ?? "",
            OperStatus = liveOperStatus,
            SpeedBps = speedBps > 0 ? speedBps : (long?)null,
            RateInBps = rateInBps,
            RateOutBps = rateOutBps,
            BytesIn = iface.InOctets,
            BytesOut = iface.OutOctets,
            UcastPktsIn = iface.InUcastPkts,
            UcastPktsOut = iface.OutUcastPkts,
            McastPktsIn = iface.InMulticastPkts,
            McastPktsOut = iface.OutMulticastPkts,
            BcastPktsIn = iface.InBroadcastPkts,
            BcastPktsOut = iface.OutBroadcastPkts,
            ErrorsIn = iface.InErrors,
            ErrorsOut = iface.OutErrors,
            DiscardsIn = iface.InDiscards,
            DiscardsOut = iface.OutDiscards,
            Time = now,
        });

        // SNMP per-interface rates feed the port-keyed cache. Match SNMP ifName to
        // UniFi PortTable.Name (both Linux names like "eth4") then write keyed by
        // PortTable.PortIdx (the UniFi-side port number the post-process looks up).
        //
        // Why name-match: PortIdx == ifIndex is true for UniFi switches but NOT
        // for gateways. On gateways UniFi numbers ports 1-7 by physical slot while
        // Linux assigns SNMP ifIndex by driver init order (lo=1, eth0=2, ...). The
        // Linux ifName is the only stable join key across both.
        //
        // Why SNMP and not UniFi port_table.tx_bytes: UniFi updates those server-
        // side at ~30s while we poll every 5s, producing a "0, 0, 0, 0, BOOM"
        // pattern (5 polls of no-change then a 30s-worth-of-bytes spike). SNMP
        // polls the device's own kernel counters at 5s and produces smooth deltas.
        if (rateInBps.HasValue && rateOutBps.HasValue && device.PortTable != null)
        {
            // Two-strategy port match (firmware varies):
            //  - Switches: SNMP ifIndex == PortTable.PortIdx. Direct numeric match.
            //  - Gateways: ifIndex != PortIdx (PortIdx is physical slot, ifIndex is
            //    Linux driver init order). PortTable entries on WAN ports carry an
            //    'ifname' field (e.g. "eth1") that joins to SNMP's iface.Name.
            // Try the numeric match first; fall back to ifname.
            // ifname match first (safer on gateways: Linux ifIndex != PortIdx, so
            // numeric match would collide - eth4 ifIndex 6 would match the port_idx
            // 6 entry which is eth5). Numeric fallback covers switches where the
            // port_table entries usually lack ifname but keep PortIdx == ifIndex.
            SwitchPort? portMatch = null;
            if (!string.IsNullOrEmpty(ifName))
            {
                portMatch = device.PortTable.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.IfName)
                    && string.Equals(p.IfName, ifName, StringComparison.OrdinalIgnoreCase)
                    && p.PortIdx > 0);
            }
            if (portMatch == null && iface.Index > 0)
                portMatch = device.PortTable.FirstOrDefault(p => p.PortIdx == iface.Index);
            if (portMatch != null && portMatch.PortIdx > 0)
            {
                _fabric.SetSnmpPortRate(mac, portMatch.PortIdx, rateInBps.Value, rateOutBps.Value);
            }
        }

        return (rateInBps, rateOutBps);
    }

    private SnmpPoller? _cachedPoller;
    private string _pollerConfigHash = string.Empty;

    private SnmpPoller? GetOrBuildPoller(MonitoringSettings settings)
    {
        var hash = ComputePollerConfigHash(settings);
        if (_cachedPoller != null && hash == _pollerConfigHash)
            return _cachedPoller;

        var poller = BuildPoller(settings);
        if (poller != null)
        {
            _cachedPoller = poller;
            _pollerConfigHash = hash;
        }
        return poller;
    }

    private static string ComputePollerConfigHash(MonitoringSettings s) =>
        $"{s.SnmpVersion}|{s.SnmpCommunity}|{s.SnmpV3Username}|{s.SnmpV3AuthPassword}|{s.MediumPollIntervalSeconds}|{s.SlowPollIntervalSeconds}";

    private SnmpPoller? BuildPoller(MonitoringSettings settings)
    {
        try
        {
            var cfg = new SnmpConfiguration
            {
                MediumPollIntervalSeconds = settings.MediumPollIntervalSeconds,
                SlowPollIntervalSeconds = settings.SlowPollIntervalSeconds
            };
            if (settings.SnmpVersion == SnmpVersionSetting.V2c)
            {
                cfg.Version = SnmpVersion.V2c;
                cfg.Community = string.IsNullOrEmpty(settings.SnmpCommunity)
                    ? string.Empty
                    : _credentialProtection.Decrypt(settings.SnmpCommunity);
                if (string.IsNullOrEmpty(cfg.Community))
                {
                    _logger.LogDebug("SNMP v2c selected but no community string available");
                    return null;
                }
            }
            else
            {
                cfg.Version = SnmpVersion.V3;
                cfg.Username = settings.SnmpV3Username ?? string.Empty;
                cfg.AuthenticationPassword = string.IsNullOrEmpty(settings.SnmpV3AuthPassword)
                    ? string.Empty
                    : _credentialProtection.Decrypt(settings.SnmpV3AuthPassword);
                if (string.IsNullOrEmpty(cfg.Username))
                {
                    _logger.LogDebug("SNMP v3 selected but no username available");
                    return null;
                }
            }

            return new SnmpPoller(cfg, _loggerFactory.CreateLogger<SnmpPoller>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to construct SnmpPoller from MonitoringSettings");
            return null;
        }
    }

    // Shared TTL cache for the UniFi device list. Every tier (fast/medium/slow/wifi)
    // calls into GetMonitorableDevicesAsync, and without a cache each call hits the
    // controller's /stat/device endpoint. TTL is hardcoded at 4s which is just under
    // the default fast tier interval (5s) - the data is always at most one fast tick
    // stale, and slower tiers piggyback on whatever the fast tier just fetched.
    // Concurrent callers serialize on _deviceFetchLock so a miss doesn't fan out.
    private static readonly TimeSpan DeviceCacheTtl = TimeSpan.FromSeconds(4);
    private List<UniFiDeviceResponse> _cachedDevices = new();
    private DateTime _cachedDevicesAt = DateTime.MinValue;
    private readonly SemaphoreSlim _deviceFetchLock = new(1, 1);

    // Gateway LAN IP cache. UniFi reports the gateway's "ip" as the WAN public IP
    // which never answers SNMP from inside the LAN. The actual SNMP-reachable IP is
    // the gateway's default-LAN address (e.g. 192.168.1.1). Resolved via network
    // configs the same way UniFiDiscovery.GetDefaultLanGatewayIp does, then reused
    // by every tier when polling gateway devices. Refresh hourly - this rarely
    // changes and the API call is heavy.
    private static readonly TimeSpan GatewayLanIpTtl = TimeSpan.FromHours(1);
    private string? _gatewayLanIp;
    private DateTime _gatewayLanIpAt = DateTime.MinValue;
    private readonly SemaphoreSlim _gatewayLanIpLock = new(1, 1);

    /// <summary>
    /// Resolves the gateway's LAN-side IP via the default-LAN network config, the same
    /// way Device Status / UniFiDiscovery does it. UniFi reports the gateway's "ip"
    /// field as the WAN public IP, which never answers SNMP from inside the LAN.
    /// Returns null if no LAN gateway can be derived (no networks fetched yet, etc).
    /// </summary>
    private async Task<string?> ResolveGatewayLanIpAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _gatewayLanIpAt < GatewayLanIpTtl) return _gatewayLanIp;

        await _gatewayLanIpLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _gatewayLanIpAt < GatewayLanIpTtl) return _gatewayLanIp;
            if (_connectionService.Client == null) return _gatewayLanIp;

            try
            {
                var ip = await Monitoring.SnmpDeviceRules.ResolveGatewayLanIpAsync(_connectionService.Client, ct);

                _gatewayLanIp = ip;
                _gatewayLanIpAt = DateTime.UtcNow;
                return ip;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve gateway LAN IP");
                return _gatewayLanIp; // keep last good
            }
        }
        finally
        {
            _gatewayLanIpLock.Release();
        }
    }

    /// <summary>
    /// Poll address for a device (SNMP and fabric latency targets). Rule shared
    /// with the agent-tunnel SNMP config push via SnmpDeviceRules.
    /// </summary>
    private static string ResolveSnmpAddress(UniFiDeviceResponse device, string? gatewayLanIp) =>
        Monitoring.SnmpDeviceRules.ResolvePollAddress(device, gatewayLanIp);

    private async Task<List<UniFiDeviceResponse>> GetMonitorableDevicesAsync(CancellationToken ct)
    {
        // The console can read disconnected between operations: an agent site's tunnel drops,
        // and a directly-connected self-hosted console returns 502/503 while its backend restarts,
        // upgrades, or reprovisions - including right when the optimizer starts up, which fails the
        // one-shot startup connect and, for the default site, previously left it dark until a
        // schedule ran or the process restarted. Reconnect it (throttled) so every tier that
        // enumerates devices - fabric targets, interface name map, device stats - recovers on its
        // own. Skipped while an agent tunnel is still coming up; OnAgentConnectedAsync reconnects
        // that case when the tunnel opens.
        if (!_connectionService.IsConnected
            && !_connectionService.IsAwaitingAgent
            && DateTime.UtcNow - _lastConsoleEnsureAt > TimeSpan.FromSeconds(30))
        {
            _lastConsoleEnsureAt = DateTime.UtcNow;
            try { await _connectionService.ReconnectAsync(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Console reconnect for device fetch failed for site {Slug}", _siteSlug);
            }
        }

        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return new List<UniFiDeviceResponse>();

        if (DateTime.UtcNow - _cachedDevicesAt < DeviceCacheTtl)
            return _cachedDevices;

        await _deviceFetchLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock: another caller may have refreshed while we waited.
            if (DateTime.UtcNow - _cachedDevicesAt < DeviceCacheTtl)
                return _cachedDevices;

            var devices = await _connectionService.Client.GetDevicesAsync(ct);
            var filtered = devices?.Where(Monitoring.SnmpDeviceRules.IsMonitorable).ToList()
                ?? new List<UniFiDeviceResponse>();
            _cachedDevices = filtered;
            _cachedDevicesAt = DateTime.UtcNow;

            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch UniFi device list for monitoring");
            return _cachedDevices; // Fall back to the last good snapshot rather than empty
        }
        finally
        {
            _deviceFetchLock.Release();
        }
    }

    private static string DescribeDeviceType(NetworkOptimizer.Core.Enums.DeviceType type) => type switch
    {
        NetworkOptimizer.Core.Enums.DeviceType.Gateway => "gateway",
        NetworkOptimizer.Core.Enums.DeviceType.Switch => "switch",
        NetworkOptimizer.Core.Enums.DeviceType.AccessPoint => "ap",
        _ => "unknown"
    };

    // Per-device-type high-temperature alert threshold (Celsius). Null falls back to
    // DeviceHealthAlertEvaluator.DefaultDeviceTempHighC inside the evaluator.
    private static double? ResolveTempThreshold(MonitoringSettings settings, NetworkOptimizer.Core.Enums.DeviceType type) =>
        type == NetworkOptimizer.Core.Enums.DeviceType.Gateway
            ? settings.GatewayTempHighC
            : settings.SwitchTempHighC;

    /// <summary>
    /// Seeds the live SFP cache from the latest persisted <c>sfp</c> points (last 6 h)
    /// so the Optical tables render immediately after a restart instead of waiting for
    /// the slow tier. Skips any port the tier has already recorded this run.
    /// </summary>
    private async Task SeedSfpLiveCacheAsync(CancellationToken ct)
    {
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);
        if (!_influx.IsConfigured) return;

        var seeded = 0;
        foreach (var point in await _influx.QueryLatestSfpAsync(ct))
        {
            if (_liveStats.GetSfpStats(point.DeviceMac, point.PortName) != null) continue;
            _liveStats.RecordSfp(
                deviceMac: point.DeviceMac,
                portName: point.PortName,
                rxDbm: point.RxPowerDbm,
                txDbm: point.TxPowerDbm,
                biasMa: point.TxBiasMa,
                tempC: point.TemperatureC,
                voltageV: point.VoltageV,
                timestamp: point.Time);
            seeded++;
        }
        if (seeded > 0)
            _logger.LogDebug("Seeded {Count} SFP live entries from InfluxDB (site {Site})", seeded, _siteSlug);
    }

    private void CollectSfpForDevice(
        UniFiDeviceResponse device,
        NetworkOptimizerDbContext db,
        Dictionary<(string DeviceMac, string PortName), MonitoredSfp> existing,
        DateTime timestamp)
    {
        if (device.PortTable == null || device.PortTable.Count == 0) return;
        var mac = NormalizeMac(device.Mac);

        foreach (var port in device.PortTable)
        {
            if (port.SfpFound != true) continue;
            // port_idx is what UniFi exposes; we use it as the stable port name for the
            // SFP measurement. This matches how the device's UI labels SFP ports too.
            var portName = port.PortIdx > 0
                ? port.PortIdx.ToString()
                : (port.Name ?? string.Empty);
            if (string.IsNullOrEmpty(portName)) continue;

            // Write the DDM values to InfluxDB regardless of whether the user has
            // promoted this SFP to a monitored ONT — having the data is what enables
            // promotion later, and the longterm bucket keeps it cheap.
            _ = _influx.WriteSfpAsync(
                deviceMac: mac,
                portName: portName,
                rxPowerDbm: port.SfpRxPower,
                txPowerDbm: port.SfpTxPower,
                txBiasMa: port.SfpCurrent,
                temperatureC: port.SfpTemperature,
                voltageV: port.SfpVoltage,
                sfpLinkSpeedMbps: port.Speed > 0 ? port.Speed : null,
                timestamp: timestamp);

            // Mirror the values into the live-stats cache so the dashboard SFP card can
            // render without a DB roundtrip on every refresh.
            _liveStats.RecordSfp(
                deviceMac: mac,
                portName: portName,
                rxDbm: port.SfpRxPower,
                txDbm: port.SfpTxPower,
                biasMa: port.SfpCurrent,
                tempC: port.SfpTemperature,
                voltageV: port.SfpVoltage,
                timestamp: timestamp);

            // Reconcile the relational row so the UI knows the SFP exists and can offer
            // ONT promotion / friendly naming.
            var key = (mac, portName);
            // Calix (and some other vendors) pad SFP vendor/part fields with trailing
            // underscores or whitespace - "CALIX___" instead of "CALIX". Trim those so
            // the UI doesn't render the padding.
            var sfpPart = TrimSfpField(port.SfpPart);
            var sfpVendor = TrimSfpField(port.SfpVendor);
            var category = IsPonModule(sfpPart, sfpVendor, port.SfpCompliance)
                ? SfpCategory.Pon : SfpCategory.Standard;
            if (!existing.TryGetValue(key, out var row))
            {
                row = new MonitoredSfp
                {
                    DeviceMac = mac,
                    PortName = portName,
                    SfpPart = sfpPart,
                    SfpVendor = sfpVendor,
                    Category = category,
                    IsMonitoredOnt = category == SfpCategory.Pon,
                    LinkSpeedMbps = port.Speed > 0 ? port.Speed : null,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };
                db.MonitoredSfps.Add(row);
                existing[key] = row;
            }
            else
            {
                row.SfpPart = sfpPart ?? row.SfpPart;
                row.SfpVendor = sfpVendor ?? row.SfpVendor;
                if (category == SfpCategory.Pon && row.Category == SfpCategory.Standard)
                    row.Category = SfpCategory.Pon;
                if (port.Speed > 0) row.LinkSpeedMbps = port.Speed;
                row.UpdatedAt = timestamp;
            }
        }
    }

    /// <summary>
    /// Detect whether an SFP module is a Passive Optical Network module. Covers GPON
    /// (2.5G), XGS-PON (10G symmetric), XG-PON (10G asymmetric), EPON, and the
    /// not-yet-shipping NG-PON2. We match conservatively on the part string and on the
    /// compliance string when present — false positives here only mean an extra dashboard
    /// row, never wrong data.
    /// </summary>
    /// <summary>
    /// Vendor-padded SFP fields trimmed for display. Calix DDM reports
    /// "CALIX___" instead of "CALIX"; other vendors do similar with spaces or nulls.
    /// </summary>
    private static string? TrimSfpField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var trimmed = value.TrimEnd('_', ' ', '\0', '\t');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool IsPonModule(string? part, string? vendor, string? compliance)
    {
        // PonVariantLabel returns "PON" only when nothing matches at all.
        // Any specific result (GPON, XGS-PON, etc.) means it's a PON module.
        var variant = Core.Helpers.NetworkFormatHelpers.PonVariantLabel(part, vendor);
        if (variant != "PON") return true;

        // Compliance string as final fallback
        if (!string.IsNullOrEmpty(compliance)
            && compliance!.Contains("PON", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Record an SNMP failure for a device. If we hit the threshold AND the device's
    /// fabric ping has succeeded recently, mark it as "no SNMP" for the rest of this
    /// app lifecycle. Pingable + repeatedly SNMP-failing is strong evidence the
    /// device just doesn't speak SNMP (USW-Flex-Mini is the canonical case).
    /// </summary>
    private void NoteSnmpFailure(string normalizedMac)
    {
        if (_snmpFailures.NoteFailure(normalizedMac))
        {
            _logger.LogWarning(
                "Excluding {Mac} from SNMP polling for {Duration} - consecutive failures hit the threshold. Will retry automatically.",
                normalizedMac, _snmpFailures.ExclusionDuration);
        }
    }

    private bool IsSnmpExcluded(string normalizedMac)
    {
        var excluded = _snmpFailures.IsExcluded(normalizedMac, out var justExpired);
        if (justExpired)
            _logger.LogInformation("SNMP exclusion expired for {Mac}, resuming polling", normalizedMac);
        return excluded;
    }

    /// <summary>Tells the dashboard which devices were dropped from SNMP polling.</summary>
    public IReadOnlyCollection<string> GetSnmpExcludedDevices() => _snmpFailures.ExcludedKeys;

    /// <summary>
    /// Per-device SNMP polling status for the Monitoring → Setup dashboard. Uses the
    /// agent's cached UniFi device list (refreshed every cycle) cross-referenced with
    /// the live failure / exclusion / last-polled state. Returns an empty list when
    /// UniFi isn't connected.
    /// </summary>
    public async Task<IReadOnlyList<SnmpDeviceStatus>> GetSnmpDeviceStatusesAsync(CancellationToken ct = default)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        // On an agent-covered site the server doesn't poll SNMP locally (the agent does),
        // so _snmpLastPolled/_snmpFailures stay empty. Fall back to the last time the agent
        // relayed SNMP data for each device so the status table reflects reality.
        var agentCovers = AgentCoversCollection();
        var result = new List<SnmpDeviceStatus>(devices.Count);
        foreach (var device in devices)
        {
            var mac = NormalizeMac(device.Mac);
            var snmpEnabled = Monitoring.SnmpDeviceRules.HasSnmpEnabled(device);
            var excluded = _snmpFailures.PeekExcluded(mac, out var excludedAt);
            var hasLastPolled = _snmpLastPolled.TryGetValue(mac, out var lastPolled);
            var failures = _snmpFailures.GetFailureCount(mac);

            if (!hasLastPolled && agentCovers)
            {
                var agentSeen = _liveStats.GetSnmpLastSeen(mac);
                if (agentSeen.HasValue)
                {
                    hasLastPolled = true;
                    lastPolled = agentSeen.Value;
                }
            }

            SnmpPollState state;
            if (!snmpEnabled) state = SnmpPollState.SnmpDisabled;
            else if (excluded) state = SnmpPollState.Excluded;
            else if (hasLastPolled) state = SnmpPollState.Polling;
            else state = SnmpPollState.NotYetPolled;

            result.Add(new SnmpDeviceStatus(
                Mac: mac,
                Name: string.IsNullOrEmpty(device.Name) ? mac : device.Name,
                DeviceType: device.DeviceType,
                SnmpEnabled: snmpEnabled,
                PollState: state,
                LastPolledUtc: hasLastPolled ? lastPolled : null,
                FailureCount: failures,
                ExcludedAtUtc: excluded ? excludedAt : null));
        }
        return result;
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');

    private async Task<Dictionary<string, List<CustomOidConfiguration>>> LoadCustomOidsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _customOidsLoadedAt < CustomOidsCacheTtl)
            return _customOidsByDevice;

        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            var all = await db.CustomOidConfigurations
                .Where(c => c.Enabled)
                .ToListAsync(ct);
            _customOidsByDevice = all
                .GroupBy(c => NormalizeMac(c.DeviceMac))
                .ToDictionary(g => g.Key, g => g.ToList());
            _customOidsLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load custom OID configurations");
        }
        return _customOidsByDevice;
    }

    private async Task PollCustomOidsAsync(
        SnmpPoller poller,
        string deviceMac,
        string deviceType,
        IPAddress ip,
        List<CustomOidConfiguration> configs,
        CancellationToken ct)
    {
        var deviceFields = new Dictionary<string, object>();
        var interfaceFields = new Dictionary<string, Dictionary<string, object>>();

        // Resolve ifIndex → ifName for interface-level OIDs using the DB name map
        Dictionary<string, string>? ifNameByIdx = null;

        foreach (var cfg in configs)
        {
            try
            {
                if (cfg.Scope == CustomOidScope.DeviceLevel)
                {
                    var value = await poller.GetAsync<string>(ip, cfg.Oid);
                    if (value != null)
                        deviceFields[cfg.FieldName] = ParseCustomValue(value, cfg.ValueType);
                }
                else
                {
                    if (ifNameByIdx == null)
                    {
                        await using var db = await CreateSiteDbAsync(ct);
                        var mac = NormalizeMac(deviceMac);
                        ifNameByIdx = await db.InterfaceNameMaps
                            .Where(m => m.DeviceMac == mac && m.IfIndex != null)
                            .ToDictionaryAsync(m => m.IfIndex!.Value.ToString(), m => m.IfName, ct);
                    }

                    var walked = await poller.BulkWalkAsync(ip, cfg.Oid);
                    foreach (var v in walked)
                    {
                        var oid = v.Id.ToString();
                        var prefix = cfg.Oid + ".";
                        if (!oid.StartsWith(prefix)) continue;
                        var ifIdx = oid.Substring(prefix.Length);
                        var ifName = ifNameByIdx.TryGetValue(ifIdx, out var name) ? name : ifIdx;
                        if (!interfaceFields.TryGetValue(ifName, out var fields))
                        {
                            fields = new Dictionary<string, object>();
                            interfaceFields[ifName] = fields;
                        }
                        fields[cfg.FieldName] = ParseCustomValue(v.Data.ToString(), cfg.ValueType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Custom OID poll failed: {Mac} OID={Oid}", deviceMac, cfg.Oid);
            }
        }

        var now = DateTime.UtcNow;

        if (deviceFields.Count > 0)
        {
            _ = _influx.WriteCustomFieldsAsync(
                "device_health", deviceMac, deviceFields, deviceType, null, null, now);
        }

        foreach (var (ifName, fields) in interfaceFields)
        {
            _ = _influx.WriteCustomFieldsAsync(
                "interface_counters", deviceMac, fields, null, ifName, null, now);
        }
    }

    private static object ParseCustomValue(string raw, CustomOidValueType valueType) =>
        CustomOidValueParser.Parse(raw, valueType);

}

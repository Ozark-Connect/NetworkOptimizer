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
    private readonly SiteConnectionRegistry _siteConnections;
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

    // Console device list + networkconf per site, cached so the agent-relayed interface
    // name-map reconcile doesn't hit the controller on every batch. Fetched through the
    // tunneled console.
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<UniFiDeviceResponse> Devices, IReadOnlyList<NetworkInfo> Networks)> _consoleCache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _consoleFetchGate = new();
    private static readonly TimeSpan ConsoleCacheTtl = TimeSpan.FromSeconds(60);

    // Per-site topology-boundary aggregator (fabric sums, AP backhaul, gateway WAN),
    // the same LanFabricAggregator the directly-monitored fast tier uses. Keyed by slug
    // because this sink is a singleton serving every agent site.
    private readonly ConcurrentDictionary<string, LanFabricAggregator> _fabricBySite = new();

    public AgentProbeResultSink(
        SiteDbContextFactory siteDbFactory,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringLiveStatsRegistry liveStatsRegistry,
        SiteConnectionRegistry siteConnections,
        MonitoringAlertRegistry alertRegistry,
        ICredentialProtectionService credentialProtection,
        ILogger<AgentProbeResultSink> logger)
    {
        _siteDbFactory = siteDbFactory;
        _influxRegistry = influxRegistry;
        _liveStatsRegistry = liveStatsRegistry;
        _siteConnections = siteConnections;
        _alertRegistry = alertRegistry;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    /// <summary>Called once per connection after the hello exchange, and by the periodic refresh.</summary>
    public async Task OnAgentConnectedAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        await PushProbeConfigAsync(connection, ct);
        await PushSnmpConfigAsync(connection, ct);

        // This site's console reaches the UniFi console THROUGH this agent tunnel.
        // On startup / after an agent restart the console auto-connect can run
        // before the tunnel is up, exhaust its short retry window, and stay
        // disconnected until a manual reconnect. Now that the tunnel is up,
        // reconnect it - fire-and-forget so we never block the tunnel read loop.
        _ = ReconnectConsoleIfViaAgentAsync(connection);
    }

    private async Task ReconnectConsoleIfViaAgentAsync(AgentTunnelConnection connection)
    {
        try
        {
            var siteConnection = _siteConnections.GetFor(connection.SiteSlug);
            if (siteConnection.IsConnected || !await siteConnection.IsConsoleViaAgentAsync())
                return;
            _logger.LogInformation(
                "Agent tunnel up for site {Slug}; reconnecting its console via the tunnel", connection.SiteSlug);
            await siteConnection.ReconnectAsync();

            // The initial SNMP push in OnAgentConnectedAsync was deferred because the
            // console wasn't connected yet (it reaches the console through this same
            // tunnel). Now that it's up, re-push so the agent gets the full device list
            // immediately instead of waiting for the next periodic refresh.
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
            var targets = await db.MonitoringTargets
                .AsNoTracking()
                .Where(t => t.Enabled)
                .ToListAsync(ct);
            var contextsById = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);

            var config = new ProbeConfig();
            foreach (var target in targets)
            {
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
            _logger.LogInformation("Pushed {Count} probe target(s) to agent {Id} (site {Slug})",
                config.Targets.Count, connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push probe config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
        }
    }

    /// <summary>
    /// Builds and pushes the site's SNMP monitoring config: credentials from
    /// the site's MonitoringSettings, device list from the site's console
    /// connection, filtered and addressed by the same SnmpDeviceRules the
    /// local collection agent uses. Default-site agents never get SNMP config
    /// - the server's own collection agent already polls those devices.
    /// </summary>
    public async Task PushSnmpConfigAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        if (connection.SiteSlug == SiteManagementService.DefaultSiteSlug) return;
        try
        {
            await using var db = _siteDbFactory.CreateForSite(connection.SiteSlug, isDefault: false);
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);

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

                // Console is up but the site genuinely has no SNMP-enabled devices.
                if (config.Devices.Count == 0)
                    config.Enabled = false;
            }

            connection.TrySend(new ServerMessage { SnmpConfig = config });
            if (config.Enabled)
                _logger.LogInformation("Pushed SNMP config with {Count} device(s) to agent {Id} (site {Slug})",
                    config.Devices.Count, connection.AgentId, connection.SiteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push SNMP config to agent {Id} (site {Slug})",
                connection.AgentId, connection.SiteSlug);
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
        var console = batch.Interfaces.Count > 0
            ? GetConsoleData(connection.SiteSlug)
            : (Devices: (IReadOnlyList<UniFiDeviceResponse>)Array.Empty<UniFiDeviceResponse>(),
               Networks: (IReadOnlyList<NetworkInfo>)Array.Empty<NetworkInfo>());
        var deviceByMac = console.Devices
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .GroupBy(d => NormalizeMac(d.Mac))
            .ToDictionary(g => g.Key, g => g.First());

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
                    .Correlate(aggDevice.PortTable, ifIndex: 0, sample.SpeedBps, sample.PortId, sample.IfName)
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
                                device.PortTable, ifIndex: 0, sample.SpeedBps, sample.PortId, sample.IfName);
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
                                if (corr.PortNumber.HasValue) mapping.PortNumber = corr.PortNumber;
                                if (corr.LinkSpeedMbps.HasValue) mapping.SpeedMbps = corr.LinkSpeedMbps;
                                if (corr.IsSfp.HasValue) mapping.IsSfp = corr.IsSfp;
                                mapping.LastUpdated = DateTime.UtcNow;
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
            await influx.WriteDeviceHealthAsync(
                deviceMac: health.DeviceMac,
                deviceType: string.IsNullOrEmpty(health.DeviceType) ? "unknown" : health.DeviceType,
                cpuPercent: health.HasCpuPercent ? health.CpuPercent : null,
                memoryTotalKb: health.HasMemoryTotalKb ? health.MemoryTotalKb : null,
                memoryUsedKb: health.HasMemoryUsedKb ? health.MemoryUsedKb : null,
                memoryUsedPercent: health.HasMemoryUsedPercent ? health.MemoryUsedPercent : null,
                temperatureC: health.HasTemperatureC ? health.TemperatureC : null,
                uptimeSeconds: health.HasUptimeSeconds ? health.UptimeSeconds : null,
                timestamp: timestamp);

            liveStats.RecordHealth(
                health.DeviceMac,
                health.HasCpuPercent ? health.CpuPercent : null,
                health.HasMemoryUsedPercent ? health.MemoryUsedPercent : null,
                health.HasTemperatureC ? health.TemperatureC : null,
                health.HasUptimeSeconds ? health.UptimeSeconds : null,
                timestamp);

            // Threshold evaluation through the site's own evaluator instance, same
            // state machine the local medium tier runs. The name cache captured on
            // config push gives the alert a device label instead of a bare MAC.
            try
            {
                string? deviceName = null;
                if (_deviceNamesBySite.TryGetValue(connection.SiteSlug, out var names))
                    names.TryGetValue(NormalizeMac(health.DeviceMac), out deviceName);
                var isGateway = string.Equals(health.DeviceType, "gateway", StringComparison.OrdinalIgnoreCase);
                await _alertRegistry.GetFor(connection.SiteSlug).DeviceHealth.EvaluateAsync(
                    health.DeviceMac, deviceName, health.DeviceType,
                    health.HasCpuPercent ? health.CpuPercent : null,
                    health.HasMemoryUsedPercent ? health.MemoryUsedPercent : null,
                    temperatureC: health.HasTemperatureC ? health.TemperatureC : null,
                    tempHighThresholdC: isGateway ? settings?.GatewayTempHighC : settings?.SwitchTempHighC,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Device health alert evaluation failed for {Mac} (site {Slug})",
                    health.DeviceMac, connection.SiteSlug);
            }
        }
    }

    /// <summary>Records a batch of probe results from an agent.</summary>
    public async Task RecordBatchAsync(AgentTunnelConnection connection, ProbeResultBatch batch, CancellationToken ct)
    {
        if (batch.Results.Count == 0) return;

        var isDefault = connection.SiteSlug == SiteManagementService.DefaultSiteSlug;
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

            if (result.Success)
                target.LastVerified = timestamp;
        }

        await db.SaveChangesAsync(ct);
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');
}

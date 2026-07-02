using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

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
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<AgentProbeResultSink> _logger;

    // Counter delta cache for agent-relayed interface samples. Key =
    // "slug/deviceMac/ifName" - same rate computation as the local fast tier.
    private readonly ConcurrentDictionary<string, InterfaceRateCalculator.State> _counterCache = new();

    public AgentProbeResultSink(
        SiteDbContextFactory siteDbFactory,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringLiveStatsRegistry liveStatsRegistry,
        SiteConnectionRegistry siteConnections,
        ICredentialProtectionService credentialProtection,
        ILogger<AgentProbeResultSink> logger)
    {
        _siteDbFactory = siteDbFactory;
        _influxRegistry = influxRegistry;
        _liveStatsRegistry = liveStatsRegistry;
        _siteConnections = siteConnections;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    /// <summary>Called once per connection after the hello exchange, and by the periodic refresh.</summary>
    public async Task OnAgentConnectedAsync(AgentTunnelConnection connection, CancellationToken ct)
    {
        await PushProbeConfigAsync(connection, ct);
        await PushSnmpConfigAsync(connection, ct);
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

            var config = new ProbeConfig();
            foreach (var target in targets)
            {
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
                if (siteConnection.IsConnected && siteConnection.Client != null)
                {
                    var devices = await siteConnection.Client.GetDevicesAsync(ct) ?? new();
                    string? gatewayLanIp = null;
                    try { gatewayLanIp = await Monitoring.SnmpDeviceRules.ResolveGatewayLanIpAsync(siteConnection.Client, ct); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Gateway LAN IP resolution failed for site {Slug}", connection.SiteSlug); }

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
                    }
                }
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

        foreach (var sample in batch.Interfaces)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(sample.TimestampUnixMs).UtcDateTime;
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
            liveStats.RecordPortStats(new MonitoringInfluxClient.PortStatsPoint
            {
                DeviceMac = sample.DeviceMac,
                IfName = sample.IfName,
                PortId = sample.PortId,
                OperStatus = sample.OperStatus,
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

        foreach (var health in batch.Health)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(health.TimestampUnixMs).UtcDateTime;
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
                timestamp: timestamp);

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

            if (result.Success)
                target.LastVerified = timestamp;
        }

        await db.SaveChangesAsync(ct);
    }
}

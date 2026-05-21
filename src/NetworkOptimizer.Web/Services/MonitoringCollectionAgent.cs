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
/// </summary>
public class MonitoringCollectionAgent : BackgroundService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly MonitoringInfluxClient _influx;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly LocalProbeExecutor _localProbe;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MonitoringCollectionAgent> _logger;

    // Counter delta cache for server-side rate computation. Key = "deviceMac/ifName".
    private readonly ConcurrentDictionary<string, CounterSnapshot> _counterCache = new();
    // Per-target last-probed time, for per-target poll intervals on a shared loop.
    private readonly ConcurrentDictionary<int, DateTime> _targetLastProbed = new();

    public MonitoringCollectionAgent(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        MonitoringInfluxClient influx,
        ICredentialProtectionService credentialProtection,
        LocalProbeExecutor localProbe,
        ILoggerFactory loggerFactory,
        ILogger<MonitoringCollectionAgent> logger)
    {
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _influx = influx;
        _credentialProtection = credentialProtection;
        _localProbe = localProbe;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Monitoring collection agent starting");

        // Seed default targets on startup so the latency tier has something to probe even
        // before the upstream wizard runs. Safe to call repeatedly — only inserts if absent.
        try { await SeedDefaultTargetsAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "Default target seeding failed"); }

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

        await Task.WhenAll(fastTask, mediumTask, slowTask, latencyTask);
        _logger.LogInformation("Monitoring collection agent stopped");
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
                var settings = await LoadSettingsAsync(stoppingToken);
                if (settings == null || !ShouldRunNow(settings))
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
                _logger.LogError(ex, "Monitoring {Tier} tier collection failed", tierName);
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
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load MonitoringSettings");
            return null;
        }
    }

    private static bool ShouldRunNow(MonitoringSettings settings)
    {
        if (!settings.Enabled) return false;
        if (settings.SnmpDetectionState != SnmpDetectionState.EnabledV2c
            && settings.SnmpDetectionState != SnmpDetectionState.EnabledV3Only
            && settings.SnmpDetectionState != SnmpDetectionState.Working)
            return false;
        if (string.IsNullOrEmpty(settings.InfluxDbToken)) return false;
        return true;
    }

    // ---- Tier collection methods ----

    private async Task FastTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        var poller = BuildPoller(settings);
        if (poller == null) return;

        // Configure InfluxDB client (no-op if already configured)
        if (!_influx.IsConfigured)
            await _influx.ReconfigureAsync(ct);

        var deviceTasks = devices.Select(async device =>
        {
            try
            {
                if (!IPAddress.TryParse(device.Ip, out var ip)) return;
                var interfaces = await poller.GetInterfaceMetricsAsync(ip, device.Name);
                var now = DateTime.UtcNow;
                foreach (var iface in interfaces)
                {
                    WriteInterfaceCounters(device, iface, now);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fast-tier interface poll failed for {Device}", device.Mac);
            }
        });
        await Task.WhenAll(deviceTasks);
    }

    private async Task MediumTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        var poller = BuildPoller(settings);
        if (poller == null) return;
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);

        var deviceTasks = devices.Select(async device =>
        {
            try
            {
                if (!IPAddress.TryParse(device.Ip, out var ip)) return;
                var metrics = await poller.GetDeviceMetricsAsync(ip, device.Name);
                if (!metrics.IsReachable) return;

                await _influx.WriteDeviceHealthAsync(
                    deviceMac: device.Mac,
                    deviceType: DescribeDeviceType(device.DeviceType),
                    cpuPercent: metrics.CpuUsage > 0 ? metrics.CpuUsage : null,
                    memoryTotalKb: metrics.TotalMemory > 0 ? metrics.TotalMemory / 1024 : null,
                    memoryUsedKb: metrics.UsedMemory > 0 ? metrics.UsedMemory / 1024 : null,
                    memoryUsedPercent: metrics.MemoryUsage > 0 ? metrics.MemoryUsage : null,
                    temperatureC: metrics.Temperature > 0 ? metrics.Temperature : null,
                    uptimeSeconds: metrics.Uptime > 0 ? metrics.Uptime / 100 : null,
                    timestamp: DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Medium-tier health poll failed for {Device}", device.Mac);
            }
        });
        await Task.WhenAll(deviceTasks);
    }

    private async Task SlowTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        var poller = BuildPoller(settings);
        if (poller == null) return;

        // Reconcile InterfaceNameMap: stable device_mac+ifName → friendly name from UniFi
        // (per spec 3.7).
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existingMaps = await db.InterfaceNameMaps.ToDictionaryAsync(
            m => (m.DeviceMac, m.IfName), m => m, ct);

        foreach (var device in devices)
        {
            try
            {
                if (!IPAddress.TryParse(device.Ip, out var ip)) continue;
                var interfaces = await poller.GetInterfaceMetricsAsync(ip, device.Name);
                foreach (var iface in interfaces)
                {
                    var ifName = string.IsNullOrEmpty(iface.Name) ? iface.Description : iface.Name;
                    if (string.IsNullOrEmpty(ifName)) continue;
                    var key = (NormalizeMac(device.Mac), ifName);

                    if (!existingMaps.TryGetValue(key, out var mapping))
                    {
                        mapping = new InterfaceNameMap
                        {
                            DeviceMac = key.Item1,
                            IfName = ifName,
                            IfIndex = iface.Index,
                            IfAlias = iface.Description,
                            SpeedMbps = (int?)(iface.HighSpeed > 0 ? iface.HighSpeed : iface.Speed / 1_000_000),
                            FriendlyName = LookupUniFiPortName(device, iface),
                            LastUpdated = DateTime.UtcNow
                        };
                        db.InterfaceNameMaps.Add(mapping);
                    }
                    else
                    {
                        mapping.IfIndex = iface.Index;
                        mapping.IfAlias = iface.Description;
                        if (iface.HighSpeed > 0) mapping.SpeedMbps = (int)iface.HighSpeed;
                        else if (iface.Speed > 0) mapping.SpeedMbps = (int)(iface.Speed / 1_000_000);
                        var unifiName = LookupUniFiPortName(device, iface);
                        if (!string.IsNullOrEmpty(unifiName)) mapping.FriendlyName = unifiName;
                        mapping.LastUpdated = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Slow-tier metadata poll failed for {Device}", device.Mac);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ---- Latency / loss tier ----

    private async Task LatencyTierCollectAsync(MonitoringSettings settings, CancellationToken ct)
    {
        if (!_influx.IsConfigured) await _influx.ReconfigureAsync(ct);

        // Reconcile fabric targets with the live device list — gateways, switches, and APs
        // should each have a `fabric` target on their management IP (spec 5.4). New devices
        // add a target; deleted devices leave their targets untouched (history preserved).
        await ReconcileFabricTargetsAsync(ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var targets = await db.MonitoringTargets
            .AsNoTracking()
            .Where(t => t.Enabled)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var dueTargets = targets.Where(t => IsDue(t, now)).ToList();
        if (dueTargets.Count == 0) return;

        // Probe in parallel but bounded so we don't fan out to dozens of pings at once on a
        // dense fabric. 8 concurrent is well under what the local executor can handle.
        using var concurrency = new SemaphoreSlim(8);
        var tasks = dueTargets.Select(t => ProbeTargetAsync(t, concurrency, ct));
        await Task.WhenAll(tasks);
    }

    private bool IsDue(MonitoringTarget target, DateTime now)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, target.PollIntervalSeconds));
        if (!_targetLastProbed.TryGetValue(target.Id, out var last)) return true;
        return now - last >= interval;
    }

    private async Task ProbeTargetAsync(MonitoringTarget target, SemaphoreSlim concurrency, CancellationToken ct)
    {
        await concurrency.WaitAsync(ct);
        try
        {
            _targetLastProbed[target.Id] = DateTime.UtcNow;

            var probeTarget = new ProbeTarget(target.Address, target.ProbeMode, target.Port);
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
                timestamp: ping.Timestamp);

            if (ping.Success)
            {
                // Persist last-verified time on success — used by both UI ("last seen") and
                // the wizard's re-validation (spec 5.5).
                try
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
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

    private async Task SeedDefaultTargetsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
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
                TargetType = MonitoringTargetType.Wan,
                VantagePoint = "server",
                PollIntervalSeconds = 10,
                PingCount = 5,
                Enabled = true,
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
                TargetType = MonitoringTargetType.Wan,
                VantagePoint = "server",
                PollIntervalSeconds = 10,
                PingCount = 5,
                Enabled = true,
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

    private async Task ReconcileFabricTargetsAsync(CancellationToken ct)
    {
        var devices = await GetMonitorableDevicesAsync(ct);
        if (devices.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existingByTargetId = await db.MonitoringTargets
            .Where(t => t.TargetType == MonitoringTargetType.Fabric)
            .ToDictionaryAsync(t => t.TargetId, ct);

        bool changed = false;
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Ip) || string.IsNullOrEmpty(d.Mac)) continue;
            var targetId = $"fabric-{NormalizeMac(d.Mac)}";
            if (existingByTargetId.TryGetValue(targetId, out var existing))
            {
                // Refresh address in case the device's management IP changed.
                if (existing.Address != d.Ip)
                {
                    existing.Address = d.Ip;
                    changed = true;
                }
                continue;
            }

            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = targetId,
                Name = string.IsNullOrEmpty(d.Name) ? d.Mac : d.Name,
                Address = d.Ip,
                ProbeMode = ProbeMode.Icmp,
                TargetType = MonitoringTargetType.Fabric,
                DeviceMac = NormalizeMac(d.Mac),
                VantagePoint = "server",
                PollIntervalSeconds = 5,
                PingCount = 3,
                Enabled = true,
                AutoDiscovered = true,
                AutoLabel = DescribeDeviceType(d.DeviceType),
                CreatedAt = DateTime.UtcNow
            });
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    // ---- Helpers ----

    private void WriteInterfaceCounters(UniFiDeviceResponse device, InterfaceMetrics iface, DateTime now)
    {
        var ifName = string.IsNullOrEmpty(iface.Name) ? iface.Description : iface.Name;
        if (string.IsNullOrEmpty(ifName)) return;
        var mac = NormalizeMac(device.Mac);

        // Compute rate from previous snapshot
        var key = $"{mac}/{ifName}";
        double? rateInBps = null;
        double? rateOutBps = null;
        if (_counterCache.TryGetValue(key, out var prev))
        {
            var elapsed = (now - prev.Timestamp).TotalSeconds;
            if (elapsed > 0.5)
            {
                // 32-bit wrap detection: if delta is negative and we know counter is 32-bit
                long deltaIn = iface.InOctets - prev.InOctets;
                long deltaOut = iface.OutOctets - prev.OutOctets;
                bool useHc = iface.HighSpeed >= 1000 || iface.Speed >= 1_000_000_000;
                if (deltaIn < 0 && !useHc) deltaIn += (long)uint.MaxValue + 1;
                if (deltaOut < 0 && !useHc) deltaOut += (long)uint.MaxValue + 1;
                if (deltaIn >= 0 && deltaOut >= 0)
                {
                    rateInBps = deltaIn * 8.0 / elapsed;
                    rateOutBps = deltaOut * 8.0 / elapsed;
                }
            }
        }
        _counterCache[key] = new CounterSnapshot(now, iface.InOctets, iface.OutOctets);

        bool hcCounters = iface.HighSpeed >= 1000 || iface.Speed >= 1_000_000_000;
        long speedBps = iface.HighSpeed > 0 ? iface.HighSpeed * 1_000_000L : iface.Speed;

        _ = _influx.WriteInterfaceCountersAsync(
            deviceMac: mac,
            ifName: ifName,
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
            timestamp: now);
    }

    private SnmpPoller? BuildPoller(MonitoringSettings settings)
    {
        try
        {
            var cfg = new SnmpConfiguration();
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

    private async Task<List<UniFiDeviceResponse>> GetMonitorableDevicesAsync(CancellationToken ct)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return new List<UniFiDeviceResponse>();
        try
        {
            var devices = await _connectionService.Client.GetDevicesAsync(ct);
            return devices?.Where(d =>
                d.Adopted && d.State == 1 && !string.IsNullOrEmpty(d.Ip) && !string.IsNullOrEmpty(d.Mac))
                .ToList() ?? new List<UniFiDeviceResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch UniFi device list for monitoring");
            return new List<UniFiDeviceResponse>();
        }
    }

    private static string DescribeDeviceType(NetworkOptimizer.Core.Enums.DeviceType type) => type switch
    {
        NetworkOptimizer.Core.Enums.DeviceType.Gateway => "gateway",
        NetworkOptimizer.Core.Enums.DeviceType.Switch => "switch",
        NetworkOptimizer.Core.Enums.DeviceType.AccessPoint => "ap",
        _ => "unknown"
    };

    private static string? LookupUniFiPortName(UniFiDeviceResponse device, InterfaceMetrics iface)
    {
        // PortTable entries on switches/gateways have user-defined per-port names. Match by
        // port index (UniFi's "port_idx") to the SNMP ifIndex when possible. For the MVP we
        // fall back to the SNMP description / name; the topology-driven match comes later.
        if (device.PortTable != null)
        {
            var match = device.PortTable.FirstOrDefault(p => p.PortIdx == iface.Index);
            if (match != null && !string.IsNullOrEmpty(match.Name)) return match.Name;
        }
        return null;
    }

    private static string NormalizeMac(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');

    private readonly record struct CounterSnapshot(DateTime Timestamp, long InOctets, long OutOctets);
}

using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Background service that polls WAN byte counters, stores snapshots,
/// calculates billing-cycle usage, and publishes alert events when thresholds are crossed.
/// </summary>
public class WanDataUsageService : BackgroundService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly IAlertEventBus _alertEventBus;
    private readonly ILogger<WanDataUsageService> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(24);

    // Serializes PollAndRecordAsync to prevent concurrent access from background loop + UI trigger
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    // Per-cycle alert dedup: tracks which WANs have already fired warning/exceeded alerts this cycle
    private readonly Dictionary<string, (DateTime CycleStart, bool WarningSent, bool ExceededSent)> _alertState = new();

    // Tracks last known billing cycle start per WAN to detect cycle rollovers
    private readonly Dictionary<string, DateTime> _lastCycleStart = new();

    private DateTime _lastPruneTime = DateTime.MinValue;

    // Cache of current usage for UI consumption
    private volatile List<WanUsageSummary> _currentUsage = [];

    public WanDataUsageService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        IAlertEventBus alertEventBus,
        ILogger<WanDataUsageService> logger)
    {
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _alertEventBus = alertEventBus;
        _logger = logger;
    }

    /// <summary>
    /// Returns the most recently computed usage summaries for all tracked WANs.
    /// Falls back to DB calculation if the background poll hasn't run yet.
    /// </summary>
    public async Task<List<WanUsageSummary>> GetCurrentUsageAsync(CancellationToken ct = default)
    {
        // Always calculate fresh from DB - it's cheap (small table, indexed)
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var configs = await db.WanDataUsageConfigs.Where(c => c.Enabled).ToListAsync(ct);
        if (configs.Count == 0)
            return [];

        var now = DateTime.UtcNow;
        var summaries = new List<WanUsageSummary>();

        foreach (var config in configs)
        {
            var (cycleStart, cycleEnd) = GetBillingCycleDates(config.BillingCycleDayOfMonth, now);
            var usedBytes = await CalculateCycleUsageAsync(db, config.WanKey, cycleStart, now, ct);
            var usedGb = Math.Max(0, usedBytes / (1024.0 * 1024.0 * 1024.0) + config.ManualAdjustmentGb);

            // Dynamic baseline check: does any snapshot have a gateway boot time within this cycle?
            // Fall back to IsBaseline for old snapshots without GatewayBootTime.
            var hasBaseline = await db.WanDataUsageSnapshots
                .AnyAsync(s => s.WanKey == config.WanKey && s.Timestamp >= cycleStart
                    && ((s.GatewayBootTime != null && s.GatewayBootTime >= cycleStart)
                        || (s.GatewayBootTime == null && s.IsBaseline)), ct);

            summaries.Add(new WanUsageSummary
            {
                WanKey = config.WanKey,
                Name = config.Name,
                UsedGb = usedGb,
                CapGb = config.DataCapGb,
                WarningThresholdPercent = config.WarningThresholdPercent,
                UsagePercent = config.DataCapGb > 0 ? usedGb / config.DataCapGb * 100.0 : 0,
                BillingCycleStart = cycleStart,
                BillingCycleEnd = cycleEnd,
                DaysRemaining = Math.Max(0, (int)Math.Ceiling((cycleEnd - now).TotalDays)),
                IsOverCap = config.DataCapGb > 0 && usedGb >= config.DataCapGb,
                IsOverWarning = config.DataCapGb > 0 && usedGb >= config.DataCapGb * config.WarningThresholdPercent / 100.0,
                Enabled = config.Enabled,
                HasBaseline = hasBaseline
            });
        }

        return summaries;
    }

    /// <summary>
    /// Gets all WAN data usage configurations.
    /// </summary>
    public async Task<List<WanDataUsageConfig>> GetAllConfigsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WanDataUsageConfigs.ToListAsync();
    }

    /// <summary>
    /// Creates or updates a WAN data usage config.
    /// </summary>
    public async Task<WanDataUsageConfig> SaveConfigAsync(WanDataUsageConfig config)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.WanDataUsageConfigs.FirstOrDefaultAsync(c => c.WanKey == config.WanKey);

        if (existing != null)
        {
            existing.Name = config.Name;
            existing.Enabled = config.Enabled;
            existing.DataCapGb = Math.Max(0, config.DataCapGb);
            existing.ManualAdjustmentGb = config.ManualAdjustmentGb;
            existing.WarningThresholdPercent = Math.Clamp(config.WarningThresholdPercent, 1, 100);
            existing.BillingCycleDayOfMonth = Math.Clamp(config.BillingCycleDayOfMonth, 1, 28);
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            config.DataCapGb = Math.Max(0, config.DataCapGb);
            config.WarningThresholdPercent = Math.Clamp(config.WarningThresholdPercent, 1, 100);
            config.BillingCycleDayOfMonth = Math.Clamp(config.BillingCycleDayOfMonth, 1, 28);
            config.CreatedAt = DateTime.UtcNow;
            config.UpdatedAt = DateTime.UtcNow;
            db.WanDataUsageConfigs.Add(config);
        }

        // Auto-enable alert rules in the same save so config + rules are atomic
        if (config.Enabled)
            await EnsureAlertRulesEnabledAsync(db);

        await db.SaveChangesAsync();

        // Invalidate cached summaries so next GetCurrentUsageAsync recalculates from DB
        _currentUsage = [];

        return existing ?? config;
    }

    /// <summary>
    /// Deletes a WAN data usage config and its snapshots.
    /// </summary>
    public async Task DeleteConfigAsync(string wanKey)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var config = await db.WanDataUsageConfigs.FirstOrDefaultAsync(c => c.WanKey == wanKey);
        if (config != null)
        {
            // Delete snapshots server-side (avoids loading potentially tens of thousands of rows)
            await db.WanDataUsageSnapshots
                .Where(s => s.WanKey == wanKey)
                .ExecuteDeleteAsync();

            db.WanDataUsageConfigs.Remove(config);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Triggers an immediate poll cycle. Used after enabling tracking to get initial data.
    /// </summary>
    public async Task TriggerPollAsync()
    {
        await _pollLock.WaitAsync();
        try
        {
            await PollAndRecordAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in triggered poll cycle");
        }
        finally
        {
            _pollLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app startup
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation("WAN Data Usage tracking service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await _pollLock.WaitAsync(stoppingToken);
            try
            {
                await PollAndRecordAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in WAN data usage poll cycle");
            }
            finally
            {
                _pollLock.Release();
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAndRecordAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var configs = await db.WanDataUsageConfigs.Where(c => c.Enabled).ToListAsync(ct);

        if (configs.Count == 0)
        {
            _currentUsage = [];
            return;
        }

        // Get WAN byte counters and gateway uptime from UniFi device data
        var (wanInterfaces, uptimeSeconds) = await GetWanInterfacesAsync(ct);
        if (wanInterfaces == null)
            return;

        // Build networkgroup-to-byte-counter lookup
        // WAN keys are "wan1","wan2",... and networkgroups are "WAN","WAN2",...
        var byteCounterByGroup = new Dictionary<string, UniFi.Models.GatewayWanInterface>(StringComparer.OrdinalIgnoreCase);
        foreach (var wan in wanInterfaces)
        {
            var ng = WanKeyToNetworkGroup(wan.Key);
            byteCounterByGroup[ng] = wan;
        }

        // Get WAN network info for status (up/down, type)
        var wanNetworks = await GetWanNetworksAsync(ct);
        var networkInfoByGroup = wanNetworks
            .Where(n => !string.IsNullOrEmpty(n.WanNetworkgroup))
            .ToDictionary(n => n.WanNetworkgroup!, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var summaries = new List<WanUsageSummary>();

        foreach (var config in configs)
        {
            // Config.WanKey stores the networkgroup (e.g., "WAN", "WAN2")
            byteCounterByGroup.TryGetValue(config.WanKey, out var wan);
            networkInfoByGroup.TryGetValue(config.WanKey, out var networkInfo);

            // Store snapshot if we have data
            var isBaseline = false;
            DateTime? gatewayBootTime = uptimeSeconds > 0 ? now.AddSeconds(-uptimeSeconds) : null;

            if (wan != null)
            {
                var lastSnapshot = await db.WanDataUsageSnapshots
                    .Where(s => s.WanKey == config.WanKey)
                    .OrderByDescending(s => s.Timestamp)
                    .FirstOrDefaultAsync(ct);

                var isReset = lastSnapshot != null &&
                    (wan.RxBytes < lastSnapshot.RxBytes || wan.TxBytes < lastSnapshot.TxBytes);

                // First snapshot for this WAN: check if gateway booted within current billing cycle.
                // If so, the raw byte counters represent all usage since boot = all usage this cycle.
                if (lastSnapshot == null && gatewayBootTime.HasValue)
                {
                    var (blCycleStart, _) = GetBillingCycleDates(config.BillingCycleDayOfMonth, now);
                    isBaseline = gatewayBootTime.Value >= blCycleStart;

                    if (isBaseline)
                        _logger.LogInformation("Using gateway uptime as baseline for {WanKey}: boot {BootTime:u}, cycle start {CycleStart:u}, {RxGb:F2} GB rx + {TxGb:F2} GB tx",
                            config.WanKey, gatewayBootTime.Value, blCycleStart, wan.RxBytes / 1_073_741_824.0, wan.TxBytes / 1_073_741_824.0);
                }

                db.WanDataUsageSnapshots.Add(new WanDataUsageSnapshot
                {
                    WanKey = config.WanKey,
                    RxBytes = wan.RxBytes,
                    TxBytes = wan.TxBytes,
                    IsCounterReset = isReset,
                    IsBaseline = isBaseline,
                    GatewayBootTime = gatewayBootTime,
                    Timestamp = now
                });
            }

            // Calculate billing cycle usage
            var (cycleStart, cycleEnd) = GetBillingCycleDates(config.BillingCycleDayOfMonth, now);

            // Reset manual adjustment when a new billing cycle starts
            if (_lastCycleStart.TryGetValue(config.WanKey, out var prevCycleStart) && prevCycleStart != cycleStart
                && config.ManualAdjustmentGb != 0)
            {
                config.ManualAdjustmentGb = 0;
                _logger.LogInformation("Billing cycle rolled over for {WanName}, reset manual adjustment to 0", config.Name);
            }
            _lastCycleStart[config.WanKey] = cycleStart;

            var usedBytes = await CalculateCycleUsageAsync(db, config.WanKey, cycleStart, now, ct);
            var usedGb = Math.Max(0, usedBytes / (1024.0 * 1024.0 * 1024.0) + config.ManualAdjustmentGb);

            // Check if this cycle has a baseline snapshot (gateway booted after cycle start).
            // Use GatewayBootTime for dynamic evaluation; fall back to IsBaseline for old snapshots without boot time.
            // Include current (unsaved) snapshot's boot time since SaveChangesAsync runs after the loop.
            var hasBaseline = (isBaseline && gatewayBootTime.HasValue && gatewayBootTime.Value >= cycleStart)
                || await db.WanDataUsageSnapshots.AnyAsync(s => s.WanKey == config.WanKey && s.Timestamp >= cycleStart
                    && ((s.GatewayBootTime != null && s.GatewayBootTime >= cycleStart)
                        || (s.GatewayBootTime == null && s.IsBaseline)), ct);

            var summary = new WanUsageSummary
            {
                WanKey = config.WanKey,
                Name = config.Name,
                WanType = wan?.Type,
                IsUp = wan?.Up ?? false,
                UsedGb = usedGb,
                CapGb = config.DataCapGb,
                WarningThresholdPercent = config.WarningThresholdPercent,
                UsagePercent = config.DataCapGb > 0 ? usedGb / config.DataCapGb * 100.0 : 0,
                BillingCycleStart = cycleStart,
                BillingCycleEnd = cycleEnd,
                DaysRemaining = Math.Max(0, (int)Math.Ceiling((cycleEnd - now).TotalDays)),
                IsOverCap = config.DataCapGb > 0 && usedGb >= config.DataCapGb,
                IsOverWarning = config.DataCapGb > 0 && usedGb >= config.DataCapGb * config.WarningThresholdPercent / 100.0,
                Enabled = config.Enabled,
                HasBaseline = hasBaseline
            };

            summaries.Add(summary);

            // Check thresholds and publish alerts
            if (config.DataCapGb > 0)
                await CheckThresholdsAsync(config, summary, cycleStart, ct);
        }

        await db.SaveChangesAsync(ct);

        _currentUsage = summaries;

        // Periodic pruning
        if (now - _lastPruneTime > PruneInterval)
        {
            await PruneOldSnapshotsAsync(db, configs, now, ct);
            _lastPruneTime = now;
        }
    }

    private async Task<(List<UniFi.Models.GatewayWanInterface>? Interfaces, long UptimeSeconds)> GetWanInterfacesAsync(CancellationToken ct)
    {
        try
        {
            var client = _connectionService.Client;
            if (client == null) return (null, 0);

            var devices = await client.GetDevicesAsync(ct);
            var gateway = devices?.FirstOrDefault(d => d.DeviceType == DeviceType.Gateway);
            if (gateway == null) return (null, 0);

            return (gateway.GetWanInterfaces(), gateway.Uptime);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch WAN interfaces for data usage tracking");
            return (null, 0);
        }
    }

    private async Task<List<UniFi.NetworkInfo>> GetWanNetworksAsync(CancellationToken ct)
    {
        try
        {
            var networks = await _connectionService.GetNetworksAsync(ct);
            return networks.Where(n => n.IsWan && n.Enabled).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch WAN networks for data usage tracking");
            return [];
        }
    }

    /// <summary>
    /// Returns live WAN interface up/down status keyed by network group (e.g., "WAN", "WAN2").
    /// </summary>
    public async Task<Dictionary<string, bool>> GetWanStatusAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var (interfaces, _) = await GetWanInterfacesAsync(ct);
        if (interfaces == null) return result;

        foreach (var wan in interfaces)
        {
            var ng = WanKeyToNetworkGroup(wan.Key);
            result[ng] = wan.Up;
        }
        return result;
    }

    /// <summary>
    /// Converts a device-level WAN key (e.g., "wan1", "wan2") to a network group (e.g., "WAN", "WAN2").
    /// This is the UniFi convention used to correlate device data with network configs.
    /// </summary>
    public static string WanKeyToNetworkGroup(string wanKey)
    {
        // "wan1" -> "WAN", "wan2" -> "WAN2", "wan3" -> "WAN3"
        if (wanKey.StartsWith("wan", StringComparison.OrdinalIgnoreCase) && wanKey.Length > 3)
        {
            var suffix = wanKey[3..];
            return suffix == "1" ? "WAN" : $"WAN{suffix}";
        }
        return wanKey.ToUpperInvariant();
    }

    /// <summary>
    /// Calculates total bytes used in the billing cycle by summing deltas between consecutive snapshots.
    /// Handles counter resets by counting usage up to the reset point.
    /// </summary>
    internal static async Task<long> CalculateCycleUsageAsync(
        NetworkOptimizerDbContext db, string wanKey, DateTime cycleStart, DateTime now, CancellationToken ct)
    {
        var snapshots = await db.WanDataUsageSnapshots
            .Where(s => s.WanKey == wanKey && s.Timestamp >= cycleStart && s.Timestamp <= now)
            .OrderBy(s => s.Timestamp)
            .ToListAsync(ct);

        return CalculateUsageFromSnapshots(snapshots, cycleStart);
    }

    /// <summary>
    /// Calculates total bytes from an ordered list of snapshots.
    /// When cycleStart is provided, baseline is evaluated dynamically using GatewayBootTime.
    /// Falls back to the stored IsBaseline flag for old snapshots without GatewayBootTime.
    /// Public for testing.
    /// </summary>
    public static long CalculateUsageFromSnapshots(List<WanDataUsageSnapshot> snapshots, DateTime? cycleStart = null)
    {
        if (snapshots.Count == 0)
            return 0;

        long totalBytes = 0;

        // Determine if the first snapshot qualifies as a baseline for this cycle.
        // Dynamic: gateway booted after cycle start → raw counters = all usage this cycle.
        // Fallback: use stored IsBaseline flag for old snapshots without GatewayBootTime.
        var first = snapshots[0];
        var isBaseline = first.GatewayBootTime.HasValue && cycleStart.HasValue
            ? first.GatewayBootTime.Value >= cycleStart.Value
            : first.IsBaseline;

        if (isBaseline)
            totalBytes = first.RxBytes + first.TxBytes;

        for (int i = 1; i < snapshots.Count; i++)
        {
            var prev = snapshots[i - 1];
            var curr = snapshots[i];

            if (curr.IsCounterReset)
            {
                // Counter reset: the current snapshot's values are post-reset (small).
                // Usage before reset is unknown - we skip this delta.
                // The last known value before reset was captured as prev, which already
                // contributed to previous deltas.
                continue;
            }

            var rxDelta = curr.RxBytes - prev.RxBytes;
            var txDelta = curr.TxBytes - prev.TxBytes;

            // Only add positive deltas (negative would indicate a missed reset detection)
            if (rxDelta > 0) totalBytes += rxDelta;
            if (txDelta > 0) totalBytes += txDelta;
        }

        return totalBytes;
    }

    /// <summary>
    /// Calculates the billing cycle start and end dates for a given billing day and reference date.
    /// Public for testing.
    /// </summary>
    public static (DateTime CycleStart, DateTime CycleEnd) GetBillingCycleDates(int billingDay, DateTime referenceDate)
    {
        billingDay = Math.Clamp(billingDay, 1, 28);

        // Use local time to determine which day of month we're on (ISP billing cycles
        // align with the user's timezone, not UTC). For a self-hosted app, server
        // local time = user's timezone. Only convert if the input is explicitly UTC.
        var localRef = referenceDate.Kind == DateTimeKind.Utc
            ? referenceDate.ToLocalTime()
            : referenceDate;
        var outputKind = referenceDate.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : referenceDate.Kind;

        DateTime cycleStart;
        if (localRef.Day >= billingDay)
        {
            cycleStart = new DateTime(localRef.Year, localRef.Month, billingDay, 0, 0, 0, DateTimeKind.Local);
        }
        else
        {
            var lastMonth = localRef.AddMonths(-1);
            cycleStart = new DateTime(lastMonth.Year, lastMonth.Month, billingDay, 0, 0, 0, DateTimeKind.Local);
        }

        var nextCycleStart = cycleStart.AddMonths(1);
        var cycleEnd = nextCycleStart.AddDays(-1);

        // Convert output to match caller's expectations
        if (referenceDate.Kind == DateTimeKind.Utc)
        {
            cycleStart = cycleStart.ToUniversalTime();
            cycleEnd = cycleEnd.ToUniversalTime();
        }
        else
        {
            // For Unspecified/Local, strip the Local kind to match input convention
            cycleStart = DateTime.SpecifyKind(cycleStart, outputKind);
            cycleEnd = DateTime.SpecifyKind(cycleEnd, outputKind);
        }

        return (cycleStart, cycleEnd);
    }

    private async Task CheckThresholdsAsync(WanDataUsageConfig config, WanUsageSummary summary,
        DateTime cycleStart, CancellationToken ct)
    {
        var key = config.WanKey;

        // Reset alert state if cycle changed
        if (_alertState.TryGetValue(key, out var state) && state.CycleStart != cycleStart)
            _alertState.Remove(key);

        if (!_alertState.TryGetValue(key, out state))
            state = (cycleStart, false, false);

        // Check exceeded (100%)
        if (summary.IsOverCap && !state.ExceededSent)
        {
            await _alertEventBus.PublishAsync(new AlertEvent
            {
                EventType = "wan.data_usage_exceeded",
                Source = "wan",
                Severity = AlertSeverity.Error,
                Title = $"WAN Data Cap Exceeded: {config.Name}",
                Message = $"{config.Name} has used {summary.UsedGb:F1} GB of {config.DataCapGb:F0} GB data cap ({summary.UsagePercent:F0}%)",
                MetricValue = summary.UsagePercent,
                ThresholdValue = 100,
                Context = new Dictionary<string, string>
                {
                    ["wanKey"] = config.WanKey,
                    ["usedGb"] = summary.UsedGb.ToString("F2"),
                    ["capGb"] = config.DataCapGb.ToString("F0"),
                    ["daysRemaining"] = summary.DaysRemaining.ToString()
                }
            }, ct);

            state = (state.CycleStart, state.WarningSent, true);
            _alertState[key] = state;
            _logger.LogWarning("WAN data cap exceeded for {WanName}: {UsedGb:F1} GB / {CapGb:F0} GB",
                config.Name, summary.UsedGb, config.DataCapGb);
        }
        // Check warning threshold
        else if (summary.IsOverWarning && !state.WarningSent)
        {
            await _alertEventBus.PublishAsync(new AlertEvent
            {
                EventType = "wan.data_usage_warning",
                Source = "wan",
                Severity = AlertSeverity.Warning,
                Title = $"WAN Data Usage Warning: {config.Name}",
                Message = $"{config.Name} has used {summary.UsedGb:F1} GB of {config.DataCapGb:F0} GB data cap ({summary.UsagePercent:F0}%), exceeding the {config.WarningThresholdPercent}% warning threshold",
                MetricValue = summary.UsagePercent,
                ThresholdValue = config.WarningThresholdPercent,
                Context = new Dictionary<string, string>
                {
                    ["wanKey"] = config.WanKey,
                    ["usedGb"] = summary.UsedGb.ToString("F2"),
                    ["capGb"] = config.DataCapGb.ToString("F0"),
                    ["daysRemaining"] = summary.DaysRemaining.ToString()
                }
            }, ct);

            state = (state.CycleStart, true, state.ExceededSent);
            _alertState[key] = state;
            _logger.LogInformation("WAN data usage warning for {WanName}: {UsedGb:F1} GB / {CapGb:F0} GB ({Percent:F0}%)",
                config.Name, summary.UsedGb, config.DataCapGb, summary.UsagePercent);
        }
    }

    private async Task PruneOldSnapshotsAsync(NetworkOptimizerDbContext db,
        List<WanDataUsageConfig> configs, DateTime now, CancellationToken ct)
    {
        try
        {
            // Keep 2 billing cycles worth of data
            var cutoff = now.AddMonths(-2);
            var deleted = await db.WanDataUsageSnapshots
                .Where(s => s.Timestamp < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                _logger.LogInformation("Pruned {Count} old WAN data usage snapshots", deleted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pruning old snapshots");
        }
    }

    /// <summary>
    /// Ensures data usage alert rules exist and are enabled. Creates them if deleted.
    /// Does NOT call SaveChangesAsync - the caller is responsible for saving.
    /// </summary>
    private static async Task EnsureAlertRulesEnabledAsync(NetworkOptimizerDbContext db)
    {
        var expected = new (string Pattern, string Name, Core.Enums.AlertSeverity Severity)[]
        {
            ("wan.data_usage_warning", "WAN Data Usage: Warning", Core.Enums.AlertSeverity.Warning),
            ("wan.data_usage_exceeded", "WAN Data Usage: Cap Exceeded", Core.Enums.AlertSeverity.Error)
        };

        var patterns = expected.Select(e => e.Pattern).ToArray();
        var existing = await db.Set<Alerts.Models.AlertRule>()
            .Where(r => patterns.Contains(r.EventTypePattern))
            .ToListAsync();

        foreach (var (pattern, name, severity) in expected)
        {
            var rule = existing.FirstOrDefault(r => r.EventTypePattern == pattern);
            if (rule != null)
            {
                if (!rule.IsEnabled)
                {
                    rule.IsEnabled = true;
                    rule.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                // Rule was deleted - re-create it enabled
                db.Set<Alerts.Models.AlertRule>().Add(new Alerts.Models.AlertRule
                {
                    Name = name,
                    IsEnabled = true,
                    EventTypePattern = pattern,
                    Source = "wan",
                    MinSeverity = severity,
                    CooldownSeconds = 86400
                });
            }
        }
    }
}

/// <summary>
/// Summary of current WAN data usage for a billing cycle.
/// </summary>
public record WanUsageSummary
{
    public string WanKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? WanType { get; init; }
    public bool IsUp { get; init; }
    public double UsedGb { get; init; }
    public double CapGb { get; init; }
    public int WarningThresholdPercent { get; init; }
    public double UsagePercent { get; init; }
    public DateTime BillingCycleStart { get; init; }
    public DateTime BillingCycleEnd { get; init; }
    public int DaysRemaining { get; init; }
    public bool IsOverCap { get; init; }
    public bool IsOverWarning { get; init; }
    public bool Enabled { get; init; }
    /// <summary>
    /// True when the first snapshot used gateway uptime as a baseline, meaning
    /// port counters captured usage back to the last gateway reboot.
    /// </summary>
    public bool HasBaseline { get; init; }
}

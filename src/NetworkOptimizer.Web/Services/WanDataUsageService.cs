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

    // Per-cycle alert dedup: tracks which WANs have already fired warning/exceeded alerts this cycle
    private readonly Dictionary<string, (DateTime CycleStart, bool WarningSent, bool ExceededSent)> _alertState = new();

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
    public async Task<List<WanUsageSummary>> GetCurrentUsageAsync()
    {
        if (_currentUsage.Count > 0)
            return _currentUsage;

        // Background poll hasn't run yet - calculate from DB snapshots
        await using var db = await _dbFactory.CreateDbContextAsync();
        var configs = await db.WanDataUsageConfigs.Where(c => c.Enabled).ToListAsync();
        if (configs.Count == 0)
            return [];

        var now = DateTime.UtcNow;
        var summaries = new List<WanUsageSummary>();

        foreach (var config in configs)
        {
            var (cycleStart, cycleEnd) = GetBillingCycleDates(config.BillingCycleDayOfMonth, now);
            var usedBytes = await CalculateCycleUsageAsync(db, config.WanKey, cycleStart, now, CancellationToken.None);
            var usedGb = usedBytes / (1024.0 * 1024.0 * 1024.0) + config.ManualAdjustmentGb;

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
                DaysRemaining = Math.Max(0, (int)(cycleEnd - now).TotalDays),
                IsOverCap = config.DataCapGb > 0 && usedGb >= config.DataCapGb,
                IsOverWarning = config.DataCapGb > 0 && usedGb >= config.DataCapGb * config.WarningThresholdPercent / 100.0,
                Enabled = config.Enabled
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
            existing.DataCapGb = config.DataCapGb;
            existing.ManualAdjustmentGb = config.ManualAdjustmentGb;
            existing.WarningThresholdPercent = Math.Clamp(config.WarningThresholdPercent, 1, 100);
            existing.BillingCycleDayOfMonth = Math.Clamp(config.BillingCycleDayOfMonth, 1, 28);
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
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
            db.WanDataUsageConfigs.Remove(config);

            // Also remove snapshots for this WAN
            var snapshots = await db.WanDataUsageSnapshots
                .Where(s => s.WanKey == wanKey)
                .ToListAsync();
            db.WanDataUsageSnapshots.RemoveRange(snapshots);

            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Triggers an immediate poll cycle. Used after enabling tracking to get initial data.
    /// </summary>
    public async Task TriggerPollAsync()
    {
        try
        {
            await PollAndRecordAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in triggered poll cycle");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for app startup
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation("WAN Data Usage tracking service started");

        while (!stoppingToken.IsCancellationRequested)
        {
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
                var isBaseline = false;
                if (lastSnapshot == null && uptimeSeconds > 0)
                {
                    var (blCycleStart, _) = GetBillingCycleDates(config.BillingCycleDayOfMonth, now);
                    var bootTime = now.AddSeconds(-uptimeSeconds);
                    isBaseline = bootTime >= blCycleStart;

                    if (isBaseline)
                        _logger.LogInformation("Using gateway uptime as baseline for {WanKey}: boot {BootTime:u}, cycle start {CycleStart:u}, {RxGb:F2} GB rx + {TxGb:F2} GB tx",
                            config.WanKey, bootTime, blCycleStart, wan.RxBytes / 1_073_741_824.0, wan.TxBytes / 1_073_741_824.0);
                }

                db.WanDataUsageSnapshots.Add(new WanDataUsageSnapshot
                {
                    WanKey = config.WanKey,
                    RxBytes = wan.RxBytes,
                    TxBytes = wan.TxBytes,
                    IsCounterReset = isReset,
                    IsBaseline = isBaseline,
                    Timestamp = now
                });
            }

            // Calculate billing cycle usage
            var (cycleStart, cycleEnd) = GetBillingCycleDates(config.BillingCycleDayOfMonth, now);
            var usedBytes = await CalculateCycleUsageAsync(db, config.WanKey, cycleStart, now, ct);
            var usedGb = usedBytes / (1024.0 * 1024.0 * 1024.0) + config.ManualAdjustmentGb;

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
                DaysRemaining = Math.Max(0, (int)(cycleEnd - now).TotalDays),
                IsOverCap = config.DataCapGb > 0 && usedGb >= config.DataCapGb,
                IsOverWarning = config.DataCapGb > 0 && usedGb >= config.DataCapGb * config.WarningThresholdPercent / 100.0,
                Enabled = config.Enabled
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

        return CalculateUsageFromSnapshots(snapshots);
    }

    /// <summary>
    /// Calculates total bytes from an ordered list of snapshots.
    /// Public for testing.
    /// </summary>
    public static long CalculateUsageFromSnapshots(List<WanDataUsageSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
            return 0;

        long totalBytes = 0;

        // If the first snapshot is a baseline, its raw bytes represent all usage since
        // gateway boot (which is within the billing cycle). Include them as starting usage.
        if (snapshots[0].IsBaseline)
            totalBytes = snapshots[0].RxBytes + snapshots[0].TxBytes;

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

        DateTime cycleStart;
        if (referenceDate.Day >= billingDay)
        {
            // Cycle started this month
            cycleStart = new DateTime(referenceDate.Year, referenceDate.Month, billingDay, 0, 0, 0, DateTimeKind.Utc);
        }
        else
        {
            // Cycle started last month
            var lastMonth = referenceDate.AddMonths(-1);
            cycleStart = new DateTime(lastMonth.Year, lastMonth.Month, billingDay, 0, 0, 0, DateTimeKind.Utc);
        }

        // Cycle ends the day before the next billing day
        var nextCycleStart = cycleStart.AddMonths(1);
        var cycleEnd = nextCycleStart.AddDays(-1);

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
}

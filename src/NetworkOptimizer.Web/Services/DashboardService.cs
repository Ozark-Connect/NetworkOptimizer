using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides aggregated dashboard data by collecting information from UniFi controllers,
/// audit services, and SQM status monitors.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly ILogger<DashboardService> _logger;
    private readonly UniFiConnectionService _connectionService;
    private readonly AuditService _auditService;
    private readonly IGatewaySpeedTestService _gatewayService;
    private readonly TcMonitorClient _tcMonitorClient;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly WiFiOptimizerService _wifiOptimizerService;
    private readonly MonitoringLiveStats _liveStats;
    private readonly Monitoring.RebootReason.DeviceRebootTracker _rebootTracker;

    public DashboardService(
        ILogger<DashboardService> logger,
        UniFiConnectionService connectionService,
        AuditService auditService,
        IGatewaySpeedTestService gatewayService,
        TcMonitorClient tcMonitorClient,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        WiFiOptimizerService wifiOptimizerService,
        MonitoringLiveStats liveStats,
        Monitoring.RebootReason.DeviceRebootTracker rebootTracker)
    {
        _logger = logger;
        _connectionService = connectionService;
        _auditService = auditService;
        _gatewayService = gatewayService;
        _tcMonitorClient = tcMonitorClient;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _wifiOptimizerService = wifiOptimizerService;
        _liveStats = liveStats;
        _rebootTracker = rebootTracker;
    }

    /// <summary>Context for the current site's database (SqmWanConfigurations are per-site).</summary>
    private NetworkOptimizerDbContext CreateSiteDb() =>
        _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    /// <summary>
    /// Retrieves comprehensive dashboard data including device counts, client counts,
    /// security audit summary, and SQM status.
    /// </summary>
    /// <returns>A <see cref="DashboardData"/> object containing all dashboard metrics.</returns>
    public async Task<DashboardData> GetDashboardDataAsync()
    {
        _logger.LogDebug("Loading dashboard data");

        var data = new DashboardData();

        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogWarning("UniFi controller not connected, returning empty dashboard");
            data.ConnectionStatus = "Disconnected";
            return data;
        }

        try
        {
            // Fetch devices using discovery service (returns proper DeviceType enum)
            var devices = await _connectionService.GetDiscoveredDevicesAsync();

            if (devices != null)
            {
                // The instant the uptimes below were read, so a reboot reason can be checked
                // against the boot the device is reporting rather than an older one.
                var uptimeReadAt = DateTime.UtcNow;

                data.DeviceCount = devices.Count;
                data.Devices = devices.Select(d =>
                {
                    var info = new DeviceInfo
                    {
                        Name = d.Name ?? d.Mac ?? "Unknown",
                        Mac = d.Mac ?? string.Empty,
                        Type = d.Type,
                        StatusInfo = UniFiDeviceStateMap.ToStatus(d.State),
                        IpAddress = d.DisplayIpAddress ?? "",
                        Model = d.FriendlyModelName,
                        Firmware = d.Firmware,
                        Uptime = FormatUptime((long?)d.Uptime.TotalSeconds),
                        UptimeSeconds = d.Uptime.TotalSeconds > 0 ? (long)d.Uptime.TotalSeconds : null,
                        SuricataUpgradeAvailable = d.SuricataUpgradeAvailable
                    };

                    var rebootReason = string.IsNullOrEmpty(d.Mac)
                        ? null
                        : _rebootTracker.GetReasonForReportedUptime(d.Mac, info.UptimeSeconds, uptimeReadAt);
                    if (rebootReason != null)
                    {
                        info.RebootReason = rebootReason.Summary;
                        info.RebootReasonDetail = rebootReason.Detail;
                        info.RebootWasUnexpected = rebootReason.IsUnexpected;
                    }

                    // Merge live monitoring data when available. Stale data is dropped by the
                    // cache's prune step; here we just ignore an entry if no fresh values landed.
                    var live = string.IsNullOrEmpty(d.Mac) ? null : _liveStats.GetForDevice(d.Mac);
                    if (live != null && live.HasFreshData(TimeSpan.FromMinutes(2)))
                    {
                        info.LiveRateInBps = live.RateInBps;
                        info.LiveRateOutBps = live.RateOutBps;
                        info.LiveLatencyMs = live.LatestRttMs;
                        info.LiveLossPercent = live.LatestLossPercent;
                        info.LiveCpuPercent = live.CpuPercent;
                        info.LiveMemoryPercent = live.MemoryUsedPercent;
                        info.LiveTemperatureC = live.TemperatureC;
                    }
                    return info;
                })
                .OrderBy(d => NetworkOptimizer.Core.Helpers.NetworkUtilities.IpSortKey(d.IpAddress), StringComparer.Ordinal)
                .ToList();

                // Count by type using enum
                data.GatewayCount = devices.Count(d => d.Type == DeviceType.Gateway);
                data.SwitchCount = devices.Count(d => d.Type == DeviceType.Switch);
                data.ApCount = devices.Count(d => d.Type == DeviceType.AccessPoint);
            }

            data.ConnectionStatus = "Connected";
            data.ControllerType = _connectionService.IsUniFiOs ? "UniFi OS" : "Standalone";

            _logger.LogDebug("Dashboard loaded: {DeviceCount} devices", data.DeviceCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading dashboard data from UniFi API");
            data.ConnectionStatus = "Error";
            data.LastError = ex.Message;
        }

        // Load audit summary (from memory cache or database)
        try
        {
            var auditSummary = await _auditService.GetAuditSummaryAsync();
            data.SecurityScore = auditSummary.Score;
            data.CriticalIssues = auditSummary.CriticalCount;
            data.WarningIssues = auditSummary.WarningCount;
            data.AlertCount = auditSummary.CriticalCount + auditSummary.WarningCount;
            data.LastAuditTime = auditSummary.LastAuditTime.HasValue
                ? FormatRelativeTime(auditSummary.LastAuditTime.Value)
                : "Never";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load audit summary");
        }

        // Get SQM status (quick check - only poll TC monitor if SQM is configured)
        try
        {
            var gatewaySettings = await _gatewayService.GetSettingsAsync();
            if (string.IsNullOrEmpty(gatewaySettings?.Host) || !gatewaySettings.HasCredentials)
            {
                data.SqmStatus = "Not Configured";
            }
            else
            {
                // Use a short-lived context to avoid disposed-context errors
                // when this is called from async void event handlers
                await using var db = CreateSiteDb();
                var sqmConfigs = await db.SqmWanConfigurations
                    .AsNoTracking()
                    .OrderBy(c => c.WanNumber)
                    .ToListAsync();
                var hasEnabledSqm = sqmConfigs.Any(c => c.Enabled);

                if (!hasEnabledSqm)
                {
                    data.SqmStatus = "Not Configured";
                }
                else
                {
                    // Poll TC Monitor directly (fast HTTP call, 2s timeout, no static cache).
                    // Route through the site's agent when it is reached that way.
                    var tcStats = await _tcMonitorClient.GetTcStatsAsync(gatewaySettings.Host, siteSlug: _siteContext.Slug);
                    var interfaces = tcStats?.GetAllInterfaces();
                    data.SqmStatus = interfaces?.Any() == true ? "Active" : "Not Deployed";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get SQM status");
            data.SqmStatus = "Unknown";
        }

        // Load Wi-Fi health score
        try
        {
            var healthScore = await _wifiOptimizerService.GetSiteHealthScoreAsync();
            if (healthScore != null)
            {
                data.WiFiHealthScore = healthScore.OverallScore;
                data.WiFiHealthGrade = healthScore.Grade;
                data.WiFiHealthIssues = healthScore.Issues
                    .Where(i => i.ShowOnOverview && !i.IsAcknowledged)
                    .OrderByDescending(i => i.Severity)
                    .Take(5)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Wi-Fi health score");
        }

        return data;
    }

    private static string FormatRelativeTime(DateTime utcTime) =>
        TimeFormatHelper.FormatRelativeTime(utcTime);

    private static string FormatUptime(long? uptimeSeconds)
    {
        if (!uptimeSeconds.HasValue || uptimeSeconds.Value <= 0)
            return "Unknown";

        return TimeFormatHelper.FormatDuration(TimeSpan.FromSeconds(uptimeSeconds.Value));
    }
}

/// <summary>
/// Contains aggregated dashboard metrics and device information.
/// </summary>
public class DashboardData
{
    public int DeviceCount { get; set; }
    public int GatewayCount { get; set; }
    public int SwitchCount { get; set; }
    public int ApCount { get; set; }
    public int ClientCount { get; set; }
    public int SecurityScore { get; set; }
    public string SqmStatus { get; set; } = "Not Configured";
    public int AlertCount { get; set; }
    public int CriticalIssues { get; set; }
    public int WarningIssues { get; set; }
    public string LastAuditTime { get; set; } = "Never";
    public string ConnectionStatus { get; set; } = "Unknown";
    public string? ControllerType { get; set; }
    public string? LastError { get; set; }
    public List<DeviceInfo> Devices { get; set; } = new();

    // Wi-Fi health
    public int? WiFiHealthScore { get; set; }
    public string? WiFiHealthGrade { get; set; }
    public List<HealthIssue> WiFiHealthIssues { get; set; } = new();
}

/// <summary>
/// Represents summary information about a network device for dashboard display.
/// </summary>
public class DeviceInfo
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public DeviceType Type { get; set; }

    /// <summary>Connection status (bucket + label) derived from the UniFi device <c>state</c>.</summary>
    public DeviceStatus StatusInfo { get; set; } = new(DeviceStatusKind.Online, "Online");

    public string IpAddress { get; set; } = "";
    public string? Model { get; set; }
    public string? Firmware { get; set; }
    public string? Uptime { get; set; }

    /// <summary>Raw uptime in seconds, used to decide how prominently to surface the reboot reason.</summary>
    public long? UptimeSeconds { get; set; }

    /// <summary>Short reason the device is on its current boot, when one has been established.</summary>
    public string? RebootReason { get; set; }

    /// <summary>Evidence behind <see cref="RebootReason"/>, shown under it in the tooltip.</summary>
    public string? RebootReasonDetail { get; set; }

    /// <summary>True when the device did not stop on purpose (power loss, hang, panic, watchdog).</summary>
    public bool RebootWasUnexpected { get; set; }

    public int? ClientCount { get; set; }

    // Live monitoring data (populated from MonitoringLiveStats when available).
    public double? LiveRateInBps { get; set; }
    public double? LiveRateOutBps { get; set; }
    public double? LiveLatencyMs { get; set; }
    public double? LiveLossPercent { get; set; }
    public double? LiveCpuPercent { get; set; }
    public double? LiveMemoryPercent { get; set; }
    public double? LiveTemperatureC { get; set; }

    /// <summary>
    /// True when this device (a gateway) has a pending CyberSecure IDS/IPS (Suricata) engine
    /// upgrade available but not yet initiated. Drives the Dashboard advisory banner and the
    /// device card's upgrade bar.
    /// </summary>
    public bool SuricataUpgradeAvailable { get; set; }

    /// <summary>
    /// Get display name for the device type
    /// </summary>
    public string TypeDisplayName => Type.ToDisplayName();
}

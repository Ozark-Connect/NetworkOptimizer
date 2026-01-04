using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;

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
    private readonly GatewaySpeedTestService _gatewayService;
    private readonly SqmService _sqmService;

    public DashboardService(
        ILogger<DashboardService> logger,
        UniFiConnectionService connectionService,
        AuditService auditService,
        GatewaySpeedTestService gatewayService,
        SqmService sqmService)
    {
        _logger = logger;
        _connectionService = connectionService;
        _auditService = auditService;
        _gatewayService = gatewayService;
        _sqmService = sqmService;
    }

    /// <summary>
    /// Retrieves comprehensive dashboard data including device counts, client counts,
    /// security audit summary, and SQM status.
    /// </summary>
    /// <returns>A <see cref="DashboardData"/> object containing all dashboard metrics.</returns>
    public async Task<DashboardData> GetDashboardDataAsync()
    {
        _logger.LogInformation("Loading dashboard data");

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
                data.DeviceCount = devices.Count;
                data.Devices = devices.Select(d => new DeviceInfo
                {
                    Name = d.Name ?? d.Mac ?? "Unknown",
                    Type = d.Type,
                    Status = d.State == 1 ? "Online" : "Offline",
                    IpAddress = d.DisplayIpAddress ?? "",
                    Model = d.FriendlyModelName,
                    Uptime = FormatUptime((long?)d.Uptime.TotalSeconds)
                })
                .OrderBy(d => ParseIpForSorting(d.IpAddress))
                .ToList();

                // Count by type using enum
                data.GatewayCount = devices.Count(d => d.Type == DeviceType.Gateway);
                data.SwitchCount = devices.Count(d => d.Type == DeviceType.Switch);
                data.ApCount = devices.Count(d => d.Type == DeviceType.AccessPoint);
            }

            data.ConnectionStatus = "Connected";
            data.ControllerType = _connectionService.IsUniFiOs ? "UniFi OS" : "Standalone";

            _logger.LogInformation("Dashboard loaded: {DeviceCount} devices", data.DeviceCount);
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

        // Get SQM status (quick check - just TC monitor, no SSH)
        try
        {
            var gatewaySettings = await _gatewayService.GetSettingsAsync();
            if (string.IsNullOrEmpty(gatewaySettings?.Host) || !gatewaySettings.HasCredentials)
            {
                data.SqmStatus = "Not Configured";
            }
            else
            {
                // Try to get TC monitor status (this is fast - just HTTP)
                var sqmData = await _sqmService.GetSqmStatusAsync();
                data.SqmStatus = sqmData.Status;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get SQM status");
            data.SqmStatus = "Unknown";
        }

        return data;
    }

    private static string FormatRelativeTime(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;

        if (elapsed.TotalMinutes < 1)
            return "Just now";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} minutes ago";
        if (elapsed.TotalHours < 24)
            return $"{(int)elapsed.TotalHours} hours ago";
        if (elapsed.TotalDays < 7)
            return $"{(int)elapsed.TotalDays} days ago";

        return utcTime.ToLocalTime().ToString("MMM dd, yyyy");
    }

    private static string FormatUptime(long? uptimeSeconds)
    {
        if (!uptimeSeconds.HasValue || uptimeSeconds.Value <= 0)
            return "Unknown";

        var ts = TimeSpan.FromSeconds(uptimeSeconds.Value);

        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays} days";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} hours";

        return $"{(int)ts.TotalMinutes} minutes";
    }

    /// <summary>
    /// Parse IP address into a sortable long value for proper numeric sorting
    /// </summary>
    private static long ParseIpForSorting(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
            return long.MaxValue; // Empty IPs sort last

        var parts = ip.Split('.');
        if (parts.Length != 4)
            return long.MaxValue;

        long result = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var octet))
                return long.MaxValue;
            result = (result << 8) | (uint)(octet & 0xFF);
        }
        return result;
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
}

/// <summary>
/// Represents summary information about a network device for dashboard display.
/// </summary>
public class DeviceInfo
{
    public string Name { get; set; } = "";
    public DeviceType Type { get; set; }
    public string Status { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string? Model { get; set; }
    public string? Uptime { get; set; }
    public int? ClientCount { get; set; }

    /// <summary>
    /// Get display name for the device type
    /// </summary>
    public string TypeDisplayName => Type switch
    {
        DeviceType.Gateway => "Gateway",
        DeviceType.Switch => "Switch",
        DeviceType.AccessPoint => "Access Point",
        DeviceType.CellularModem => "Cellular Modem",
        _ => "Unknown"
    };
}

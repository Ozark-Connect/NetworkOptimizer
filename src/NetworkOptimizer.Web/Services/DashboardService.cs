using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Web.Services;

public class DashboardService
{
    private readonly ILogger<DashboardService> _logger;
    private readonly UniFiConnectionService _connectionService;

    public DashboardService(ILogger<DashboardService> logger, UniFiConnectionService connectionService)
    {
        _logger = logger;
        _connectionService = connectionService;
    }

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
            // Fetch real device data from UniFi API
            var devices = await _connectionService.Client.GetDevicesAsync();

            if (devices != null)
            {
                data.DeviceCount = devices.Count;
                data.Devices = devices.Select(d => new DeviceInfo
                {
                    Name = d.Name ?? d.Mac ?? "Unknown",
                    Type = GetDeviceType(d.Type),
                    Status = d.State == 1 ? "Online" : "Offline",
                    IpAddress = d.Ip ?? "",
                    Model = d.FriendlyModelName, // Uses shortname if available
                    Uptime = FormatUptime(d.Uptime)
                }).ToList();

                // Count by type
                data.GatewayCount = devices.Count(d => d.Type is "udm" or "ugw" or "uxg");
                data.SwitchCount = devices.Count(d => d.Type == "usw");
                data.ApCount = devices.Count(d => d.Type == "uap");
            }

            // Get client count (in try/catch since client API can have parsing issues)
            try
            {
                var clients = await _connectionService.Client.GetClientsAsync();
                data.ClientCount = clients?.Count ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get client count, continuing without it");
                data.ClientCount = 0;
            }

            data.ConnectionStatus = "Connected";
            data.ControllerType = _connectionService.IsUniFiOs ? "UniFi OS" : "Standalone";

            _logger.LogInformation("Dashboard loaded: {DeviceCount} devices, {ClientCount} clients",
                data.DeviceCount, data.ClientCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading dashboard data from UniFi API");
            data.ConnectionStatus = "Error";
            data.LastError = ex.Message;
        }

        return data;
    }

    private static string GetDeviceType(string? type) => type switch
    {
        "udm" or "ugw" or "uxg" => "Gateway",
        "usw" => "Switch",
        "uap" => "Access Point",
        "umbb" => "Cellular Modem",
        _ => type ?? "Unknown"
    };

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
}

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

public class DeviceInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string? Model { get; set; }
    public string? Uptime { get; set; }
    public int? ClientCount { get; set; }
}

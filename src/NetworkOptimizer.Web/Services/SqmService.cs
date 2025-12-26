using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing SQM (Smart Queue Management) and polling TC stats.
///
/// SQM data is obtained by polling the tc-monitor endpoint on the UniFi gateway.
/// The tc-monitor script must be deployed to /data/on_boot.d/ on the gateway.
/// It exposes TC class rates via HTTP on port 8088.
/// </summary>
public class SqmService
{
    private readonly ILogger<SqmService> _logger;
    private readonly UniFiConnectionService _connectionService;
    private readonly TcMonitorClient _tcMonitorClient;
    private readonly IServiceProvider _serviceProvider;

    // Track SQM state
    private SqmConfiguration? _currentConfig;
    private TcMonitorResponse? _lastTcStats;
    private DateTime? _lastPollTime;

    // TC Monitor settings
    private string? _tcMonitorHost;
    private int _tcMonitorPort = TcMonitorClient.DefaultPort;

    public SqmService(
        ILogger<SqmService> logger,
        UniFiConnectionService connectionService,
        TcMonitorClient tcMonitorClient,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _connectionService = connectionService;
        _tcMonitorClient = tcMonitorClient;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Configure the TC monitor endpoint to poll
    /// </summary>
    public void ConfigureTcMonitor(string host, int port = 8088)
    {
        _tcMonitorHost = host;
        _tcMonitorPort = port;
        _logger.LogInformation("TC Monitor configured: {Host}:{Port}", host, port);
    }

    /// <summary>
    /// Get current SQM status including live TC rates if available
    /// </summary>
    public async Task<SqmStatusData> GetSqmStatusAsync()
    {
        _logger.LogDebug("Loading SQM status data");

        // Check if controller is connected
        if (!_connectionService.IsConnected)
        {
            return new SqmStatusData
            {
                Status = "Unavailable",
                StatusMessage = "Connect to UniFi controller first"
            };
        }

        // Get gateway host for TC Monitor
        var gatewayHost = _tcMonitorHost ?? await GetGatewayHostAsync();

        // Check if gateway is configured
        if (string.IsNullOrEmpty(gatewayHost))
        {
            return new SqmStatusData
            {
                Status = "Not Configured",
                StatusMessage = "Gateway SSH not configured. Go to Settings to configure your gateway connection."
            };
        }

        // Try to get TC stats from the gateway
        var tcStats = await _tcMonitorClient.GetTcStatsAsync(gatewayHost, _tcMonitorPort);

        if (tcStats != null)
        {
            _lastTcStats = tcStats;
            _lastPollTime = DateTime.UtcNow;
        }

        if (tcStats == null)
        {
            return new SqmStatusData
            {
                Status = "Offline",
                StatusMessage = $"Cannot reach TC Monitor at {gatewayHost}:{_tcMonitorPort}. Deploy tc-monitor script via the SQM Manager."
            };
        }

        // Build response from live TC data (handles both legacy wan1/wan2 and new interfaces format)
        var interfaces = tcStats.GetAllInterfaces();
        var primaryWan = interfaces.FirstOrDefault(i => i.Status == "active");

        return new SqmStatusData
        {
            Status = "Active",
            CurrentRate = primaryWan?.RateMbps ?? 0,
            BaselineRate = _currentConfig?.DownloadSpeed ?? primaryWan?.RateMbps ?? 0,
            CurrentLatency = 0, // TODO: Get from latency monitoring
            LastAdjustment = _lastPollTime?.ToString("HH:mm:ss") ?? "Never",
            IsLearning = false,
            LearningProgress = 100,
            HoursLearned = 168,
            TcInterfaces = interfaces,
            TcMonitorTimestamp = tcStats.Timestamp
        };
    }

    /// <summary>
    /// Poll TC stats from the configured gateway
    /// </summary>
    private async Task<TcMonitorResponse?> PollTcStatsAsync()
    {
        // If no explicit host configured, try to use the gateway SSH host
        var host = _tcMonitorHost;
        if (string.IsNullOrEmpty(host))
        {
            host = await GetGatewayHostAsync();
        }

        if (string.IsNullOrEmpty(host))
            return null;

        var stats = await _tcMonitorClient.GetTcStatsAsync(host, _tcMonitorPort);

        if (stats != null)
        {
            _lastTcStats = stats;
            _lastPollTime = DateTime.UtcNow;
        }

        return stats;
    }

    /// <summary>
    /// Get the gateway host from SSH settings
    /// </summary>
    private async Task<string?> GetGatewayHostAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
            var settings = await repository.GetGatewaySshSettingsAsync();
            if (!string.IsNullOrEmpty(settings?.Host))
            {
                _logger.LogDebug("Using gateway SSH host for TC monitor: {Host}", settings.Host);
                return settings.Host;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get gateway SSH settings");
        }
        return null;
    }

    /// <summary>
    /// Check if TC monitor is reachable on the gateway
    /// </summary>
    public async Task<(bool Available, string? Error)> TestTcMonitorAsync(string? host = null, int? port = null)
    {
        var testHost = host ?? _tcMonitorHost;
        var testPort = port ?? _tcMonitorPort;

        if (string.IsNullOrEmpty(testHost))
        {
            // Try controller host
            var controllerUrl = _connectionService.CurrentConfig?.ControllerUrl;
            if (!string.IsNullOrEmpty(controllerUrl))
            {
                try
                {
                    testHost = new Uri(controllerUrl).Host;
                }
                catch
                {
                    return (false, "No host configured and cannot parse controller URL");
                }
            }
            else
            {
                return (false, "No host configured");
            }
        }

        var available = await _tcMonitorClient.IsMonitorAvailableAsync(testHost, testPort);

        if (available)
        {
            return (true, null);
        }

        return (false, $"TC Monitor not responding at {testHost}:{testPort}");
    }

    /// <summary>
    /// Get just the TC interface stats
    /// </summary>
    public async Task<List<TcInterfaceStats>?> GetTcInterfaceStatsAsync()
    {
        var stats = await PollTcStatsAsync();
        return stats?.Interfaces;
    }

    /// <summary>
    /// Get WAN interface configurations from the UniFi controller
    /// Returns a mapping of interface name to friendly name (e.g., "eth4" -> "Yelcot")
    /// </summary>
    public async Task<List<WanInterfaceInfo>> GetWanInterfacesFromControllerAsync()
    {
        var result = new List<WanInterfaceInfo>();

        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogWarning("Cannot get WAN interfaces: controller not connected");
            return result;
        }

        try
        {
            var wanConfigs = await _connectionService.Client.GetWanConfigsAsync();

            foreach (var wan in wanConfigs)
            {
                // The TC monitor uses "ifb" + interface name format on UDM
                // e.g., eth4 -> ifbeth4, eth0 -> ifbeth0
                var tcInterface = !string.IsNullOrEmpty(wan.WanIfname)
                    ? $"ifb{wan.WanIfname}"
                    : null;

                result.Add(new WanInterfaceInfo
                {
                    Name = wan.Name,
                    Interface = wan.WanIfname ?? "",
                    TcInterface = tcInterface ?? "",
                    WanType = wan.WanType ?? "dhcp",
                    LoadBalanceType = wan.WanLoadBalanceType,
                    LoadBalanceWeight = wan.WanLoadBalanceWeight
                });

                _logger.LogDebug("WAN interface: {Name} -> {Interface} (TC: {TcInterface})",
                    wan.Name, wan.WanIfname, tcInterface);
            }

            _logger.LogInformation("Found {Count} WAN interfaces from controller", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching WAN interfaces from controller");
        }

        return result;
    }

    /// <summary>
    /// Generate the tc-monitor configuration content based on controller WAN settings
    /// This can be used to deploy the correct interface mapping to gateways
    /// </summary>
    public async Task<string> GenerateTcMonitorConfigAsync()
    {
        var wans = await GetWanInterfacesFromControllerAsync();

        if (wans.Count == 0)
        {
            return "# No WAN interfaces found in controller configuration\n# Format: interface:name\nifbeth2:WAN1 ifbeth0:WAN2";
        }

        // Generate interface configuration in the format expected by tc-monitor
        // Format: "ifbeth4:Yelcot ifbeth0:Starlink"
        var config = string.Join(" ", wans
            .Where(w => !string.IsNullOrEmpty(w.TcInterface))
            .Select(w => $"{w.TcInterface}:{w.Name}"));

        return config;
    }

    public async Task<bool> DeploySqmAsync(SqmConfiguration config)
    {
        _logger.LogInformation("Deploying SQM configuration: {@Config}", config);

        if (!_connectionService.IsConnected)
        {
            _logger.LogWarning("Cannot deploy SQM: controller not connected");
            return false;
        }

        // TODO: When agent infrastructure is ready:
        // - Generate SQM scripts using NetworkOptimizer.Sqm.ScriptGenerator
        // - Deploy via SSH using NetworkOptimizer.Agents
        // - Verify deployment success

        await Task.Delay(2000); // Simulate deployment

        _currentConfig = config;

        return true;
    }

    public async Task<string> GenerateSqmScriptsAsync(SqmConfiguration config)
    {
        _logger.LogInformation("Generating SQM scripts for configuration: {@Config}", config);

        // TODO: Use NetworkOptimizer.Sqm.ScriptGenerator

        await Task.Delay(500); // Simulate generation

        return "/downloads/sqm-scripts.tar.gz";
    }

    public async Task<bool> DisableSqmAsync()
    {
        _logger.LogInformation("Disabling SQM");

        if (!_connectionService.IsConnected)
        {
            _logger.LogWarning("Cannot disable SQM: controller not connected");
            return false;
        }

        await Task.Delay(1000); // Simulate operation

        return true;
    }

    public async Task<SpeedtestResult> RunSpeedtestAsync()
    {
        _logger.LogInformation("Running speedtest");

        if (!_connectionService.IsConnected)
        {
            throw new InvalidOperationException("Cannot run speedtest: controller not connected");
        }

        // TODO: Trigger speedtest on agent

        await Task.Delay(15000); // Simulate speedtest duration

        return new SpeedtestResult
        {
            Timestamp = DateTime.UtcNow,
            Download = 285.4,
            Upload = 35.2,
            Latency = 12.5,
            Server = "Speedtest Server"
        };
    }
}

public class SqmStatusData
{
    public string Status { get; set; } = "";
    public string? StatusMessage { get; set; }
    public double CurrentRate { get; set; }
    public double BaselineRate { get; set; }
    public double CurrentLatency { get; set; }
    public string LastAdjustment { get; set; } = "";
    public bool IsLearning { get; set; }
    public int LearningProgress { get; set; }
    public int HoursLearned { get; set; }
    public List<SpeedtestResult> SpeedtestHistory { get; set; } = new();
    public BaselineStats BaselineStats { get; set; } = new();

    // Live TC data
    public List<TcInterfaceStats>? TcInterfaces { get; set; }
    public DateTime? TcMonitorTimestamp { get; set; }
}

public class SqmConfiguration
{
    public string Interface { get; set; } = "";
    public int DownloadSpeed { get; set; }
    public int UploadSpeed { get; set; }
    public bool EnableSpeedtest { get; set; }
    public bool EnableLatencyMonitoring { get; set; }
    public string BlendingRatio { get; set; } = "6040";
}

public class SpeedtestResult
{
    public DateTime Timestamp { get; set; }
    public double Download { get; set; }
    public double Upload { get; set; }
    public double Latency { get; set; }
    public string Server { get; set; } = "";
}

public class BaselineStats
{
    public double MeanDownload { get; set; }
    public double StdDev { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
}

/// <summary>
/// Information about a WAN interface from the UniFi controller
/// </summary>
public class WanInterfaceInfo
{
    /// <summary>Friendly name from controller (e.g., "Yelcot", "Starlink")</summary>
    public string Name { get; set; } = "";

    /// <summary>Physical interface name (e.g., "eth4", "eth0")</summary>
    public string Interface { get; set; } = "";

    /// <summary>TC monitor interface name (e.g., "ifbeth4", "ifbeth0")</summary>
    public string TcInterface { get; set; } = "";

    /// <summary>WAN connection type (dhcp, static, pppoe)</summary>
    public string WanType { get; set; } = "";

    /// <summary>Load balance type (failover-only or weighted)</summary>
    public string? LoadBalanceType { get; set; }

    /// <summary>Load balance weight (if weighted)</summary>
    public int? LoadBalanceWeight { get; set; }
}

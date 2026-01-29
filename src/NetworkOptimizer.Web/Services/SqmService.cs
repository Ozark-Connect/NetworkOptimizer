using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing SQM (Smart Queue Management) and polling TC stats.
///
/// SQM data is obtained by polling the tc-monitor endpoint on the UniFi gateway.
/// The tc-monitor script must be deployed to /data/on_boot.d/ on the gateway.
/// It exposes TC class rates via HTTP on port 8088.
/// </summary>
public class SqmService : ISqmService
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

    // Cache for SQM status (avoids repeated HTTP calls)
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromMinutes(2);
    private static SqmStatusData? _cachedStatusData;
    private static DateTime _lastStatusCheck = DateTime.MinValue;

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
    /// Get current SQM status including live TC rates if available.
    /// Results are cached for 5 minutes to avoid repeated HTTP calls.
    /// </summary>
    public async Task<SqmStatusData> GetSqmStatusAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedStatusData != null &&
            DateTime.UtcNow - _lastStatusCheck < StatusCacheDuration)
        {
            _logger.LogDebug("Returning cached SQM status");
            return _cachedStatusData;
        }

        _logger.LogDebug("Loading SQM status data (cache miss or force refresh)");

        SqmStatusData result;

        // Gateway host from database settings - doesn't require active controller connection
        var gatewayHost = _tcMonitorHost ?? await GetGatewayHostAsync();

        if (string.IsNullOrEmpty(gatewayHost))
        {
            result = new SqmStatusData
            {
                Status = "Not Configured",
                StatusMessage = "Gateway SSH not configured. Go to Settings to configure your gateway connection."
            };
            CacheStatusResult(result);
            return result;
        }

        var tcStats = await _tcMonitorClient.GetTcStatsAsync(gatewayHost, _tcMonitorPort);

        if (tcStats != null)
        {
            _lastTcStats = tcStats;
            _lastPollTime = DateTime.UtcNow;
        }

        if (tcStats == null)
        {
            result = new SqmStatusData
            {
                Status = "Offline",
                StatusMessage = "TC Monitor not running"
            };
            CacheStatusResult(result);
            return result;
        }

        // Build response from live TC data (handles both legacy wan1/wan2 and new interfaces format)
        var interfaces = tcStats.GetAllInterfaces();
        var primaryWan = interfaces.FirstOrDefault(i => i.Status == "active");

        result = new SqmStatusData
        {
            Status = "Active",
            CurrentRate = primaryWan?.RateMbps ?? 0,
            BaselineRate = _currentConfig?.DownloadSpeed ?? primaryWan?.RateMbps ?? 0,
            // TODO(latency-monitoring): Get real latency from agent metrics.
            // Requires: Agent infrastructure pushing latency samples to /api/metrics endpoint.
            CurrentLatency = 0,
            LastAdjustment = _lastPollTime?.ToString("HH:mm:ss") ?? "Never",
            IsLearning = false,
            LearningProgress = 100,
            HoursLearned = 168,
            TcInterfaces = interfaces,
            TcMonitorTimestamp = tcStats.Timestamp
        };
        CacheStatusResult(result);
        return result;
    }

    private static void CacheStatusResult(SqmStatusData result)
    {
        _cachedStatusData = result;
        _lastStatusCheck = DateTime.UtcNow;
    }

    /// <summary>
    /// Invalidate the SQM status cache (call after deploy/remove)
    /// </summary>
    public static void InvalidateStatusCache()
    {
        _cachedStatusData = null;
        _lastStatusCheck = DateTime.MinValue;
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
            // Get WAN interfaces from device data (wan1, wan2, wan3 with uplink_ifname and ip)
            var deviceJson = await _connectionService.Client.GetDevicesRawJsonAsync();
            if (string.IsNullOrEmpty(deviceJson))
            {
                _logger.LogWarning("No device data available");
                return result;
            }

            // Get WAN network configs for friendly names and SmartQ status
            var wanConfigs = await _connectionService.Client.GetWanConfigsAsync();

            // Build lookup by IP (for WANs with static IPs)
            var ipToName = wanConfigs
                .Where(w => !string.IsNullOrEmpty(w.WanIp))
                .ToDictionary(w => w.WanIp!, w => w.Name);

            // Build lookup by wan_networkgroup for SmartQ status (e.g., "WAN" -> true, "WAN2" -> true)
            var networkGroupToSmartq = wanConfigs
                .Where(w => !string.IsNullOrEmpty(w.WanNetworkgroup))
                .ToDictionary(w => w.WanNetworkgroup!, w => w.WanSmartqEnabled, StringComparer.OrdinalIgnoreCase);

            // Build lookup by wan_networkgroup for friendly name
            var networkGroupToName = wanConfigs
                .Where(w => !string.IsNullOrEmpty(w.WanNetworkgroup))
                .ToDictionary(w => w.WanNetworkgroup!, w => w.Name, StringComparer.OrdinalIgnoreCase);

            // Build lookup by wan_networkgroup for WAN type (dhcp, static, pppoe)
            var networkGroupToWanType = wanConfigs
                .Where(w => !string.IsNullOrEmpty(w.WanNetworkgroup) && !string.IsNullOrEmpty(w.WanType))
                .ToDictionary(w => w.WanNetworkgroup!, w => w.WanType!, StringComparer.OrdinalIgnoreCase);

            result = ExtractWanInterfacesFromDeviceData(deviceJson, ipToName, networkGroupToSmartq, networkGroupToName, networkGroupToWanType);

            _logger.LogInformation("Found {Count} WAN interfaces from device data", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching WAN interfaces from controller");
        }

        return result;
    }

    /// <summary>
    /// Extract WAN interfaces from device data (wan1, wan2, wan3 with uplink_ifname)
    /// Uses ethernet_overrides to map interface -> networkgroup, then looks up SmartQ status.
    /// For PPPoE connections, uses the physical interface (ifname) for networkgroup lookup
    /// but the tunnel interface (uplink_ifname, e.g., ppp3) for the actual SQM interface.
    /// </summary>
    private List<WanInterfaceInfo> ExtractWanInterfacesFromDeviceData(
        string deviceJson,
        Dictionary<string, string> ipToName,
        Dictionary<string, bool> networkGroupToSmartq,
        Dictionary<string, string> networkGroupToName,
        Dictionary<string, string> networkGroupToWanType)
    {
        var result = new List<WanInterfaceInfo>();

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(deviceJson);
            var root = doc.RootElement;

            // Handle both {data: [...]} and [...] formats
            var devices = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root
                : root.TryGetProperty("data", out var data) ? data : root;

            foreach (var device in devices.EnumerateArray())
            {
                // Only look at gateways
                var deviceType = device.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (deviceType != "ugw" && deviceType != "udm" && deviceType != "uxg")
                    continue;

                // Build ifname -> networkgroup lookup from ethernet_overrides
                var ifnameToNetworkGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (device.TryGetProperty("ethernet_overrides", out var ethOverrides) &&
                    ethOverrides.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var ov in ethOverrides.EnumerateArray())
                    {
                        var ifn = ov.TryGetProperty("ifname", out var ifnProp) ? ifnProp.GetString() : null;
                        var ng = ov.TryGetProperty("networkgroup", out var ngProp) ? ngProp.GetString() : null;
                        if (!string.IsNullOrEmpty(ifn) && !string.IsNullOrEmpty(ng))
                        {
                            ifnameToNetworkGroup[ifn] = ng;
                        }
                    }
                }

                // Check for wan1, wan2, wan3, etc.
                for (int i = 1; i <= 4; i++)
                {
                    var wanKey = $"wan{i}";
                    if (device.TryGetProperty(wanKey, out var wanObj))
                    {
                        // Get the uplink interface name (this is the actual interface we configure SQM on)
                        // For PPPoE, this will be "ppp3" (the tunnel), not "eth6" (the physical port)
                        string? uplinkIfname = null;
                        if (wanObj.TryGetProperty("uplink_ifname", out var uplinkProp))
                            uplinkIfname = uplinkProp.GetString();

                        if (string.IsNullOrEmpty(uplinkIfname))
                            continue;

                        // Get the physical interface name (used for networkgroup lookup in ethernet_overrides)
                        // For PPPoE on eth6, uplink_ifname="ppp3" but ifname="eth6"
                        string? physicalIfname = null;
                        if (wanObj.TryGetProperty("ifname", out var ifnameProp))
                            physicalIfname = ifnameProp.GetString();
                        if (string.IsNullOrEmpty(physicalIfname) && wanObj.TryGetProperty("name", out var nameProp))
                            physicalIfname = nameProp.GetString();

                        // Get WAN IP to correlate with network config name
                        string? wanIp = null;
                        if (wanObj.TryGetProperty("ip", out var ipProp))
                            wanIp = ipProp.GetString();

                        // Get networkgroup for this interface from ethernet_overrides
                        // Use physical interface for lookup (e.g., "eth6" not "ppp3" for PPPoE)
                        string? networkGroup = null;
                        var lookupIfname = physicalIfname ?? uplinkIfname;
                        if (ifnameToNetworkGroup.TryGetValue(lookupIfname, out var ng))
                            networkGroup = ng;

                        // Try to get friendly name: first from networkgroup lookup, then IP lookup, then fallback
                        var friendlyName = wanKey.ToUpper();
                        if (!string.IsNullOrEmpty(networkGroup) && networkGroupToName.TryGetValue(networkGroup, out var ngName))
                        {
                            friendlyName = ngName;
                        }
                        else if (!string.IsNullOrEmpty(wanIp) && ipToName.TryGetValue(wanIp, out var configName))
                        {
                            friendlyName = configName;
                        }

                        // Extract ISP info from mac_table
                        string? suggestedPingIp = null;
                        if (wanObj.TryGetProperty("mac_table", out var macTable) && macTable.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var entry in macTable.EnumerateArray())
                            {
                                var hostname = entry.TryGetProperty("hostname", out var hostProp) ? hostProp.GetString() : null;
                                var entryIp = entry.TryGetProperty("ip", out var entryIpProp) ? entryIpProp.GetString() : null;

                                // Try to extract ISP name from hostname if we still have default name
                                if (friendlyName == wanKey.ToUpper() && !string.IsNullOrEmpty(hostname) && hostname != "?")
                                {
                                    var ispName = ExtractIspNameFromHostname(hostname);
                                    if (!string.IsNullOrEmpty(ispName))
                                    {
                                        friendlyName = ispName;
                                    }
                                }

                                // Get non-private IP for ping monitoring (prefer public IPs)
                                if (!string.IsNullOrEmpty(entryIp) && suggestedPingIp == null)
                                {
                                    if (!IsPrivateIp(entryIp))
                                    {
                                        suggestedPingIp = entryIp;
                                    }
                                }
                            }
                        }

                        // TC monitor uses "ifb" + interface name format
                        var tcInterface = $"ifb{uplinkIfname}";

                        // Check if Smart Queues is enabled via networkgroup lookup
                        var smartqEnabled = !string.IsNullOrEmpty(networkGroup) &&
                            networkGroupToSmartq.TryGetValue(networkGroup, out var sqEnabled) && sqEnabled;

                        // Get the actual WAN type from network config (dhcp, static, pppoe)
                        var wanType = "dhcp"; // default
                        if (!string.IsNullOrEmpty(networkGroup) &&
                            networkGroupToWanType.TryGetValue(networkGroup, out var wt))
                        {
                            wanType = wt;
                        }

                        result.Add(new WanInterfaceInfo
                        {
                            Name = friendlyName,
                            Interface = uplinkIfname,
                            TcInterface = tcInterface,
                            WanType = wanType,
                            LoadBalanceType = null,
                            LoadBalanceWeight = null,
                            SuggestedPingIp = suggestedPingIp,
                            SmartqEnabled = smartqEnabled
                        });

                        _logger.LogDebug("Found {WanKey}: {Interface} -> {Name} (NetworkGroup: {NG}, SmartQ: {SQ}, WanType: {WT})",
                            wanKey, uplinkIfname, friendlyName, networkGroup, smartqEnabled, wanType);
                    }
                }

                // Found gateway, no need to check other devices
                if (result.Count > 0)
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error extracting WAN interfaces from device data");
        }

        return result;
    }

    /// <summary>
    /// Extract ISP name from gateway hostname (e.g., "cgnat-gw01.chi.starlink.net" -> "Starlink")
    /// </summary>
    private string? ExtractIspNameFromHostname(string hostname)
    {
        if (string.IsNullOrEmpty(hostname) || hostname == "?")
            return null;

        var lower = hostname.ToLowerInvariant();

        // Known ISP patterns
        if (lower.Contains("starlink"))
            return "Starlink";
        if (lower.Contains("yelcot"))
            return "Yelcot";
        if (lower.Contains("comcast") || lower.Contains("xfinity"))
            return "Xfinity";
        if (lower.Contains("spectrum") || lower.Contains("charter"))
            return "Spectrum";
        if (lower.Contains("att.net") || lower.Contains("sbcglobal"))
            return "AT&T";
        if (lower.Contains("verizon") || lower.Contains("fios"))
            return "Verizon";
        if (lower.Contains("cox.net"))
            return "Cox";
        if (lower.Contains("centurylink") || lower.Contains("lumen"))
            return "CenturyLink";
        if (lower.Contains("frontier"))
            return "Frontier";
        if (lower.Contains("t-mobile") || lower.Contains("tmobile"))
            return "T-Mobile";

        // Try to extract from domain (second-to-last segment before TLD)
        var parts = hostname.Split('.');
        if (parts.Length >= 2)
        {
            // Get the second-to-last part (e.g., "yelcot" from "yellville-cmts.yelcot.net")
            var ispPart = parts[^2];
            if (ispPart.Length >= 3 && ispPart != "com" && ispPart != "net" && ispPart != "org")
            {
                // Capitalize first letter
                return char.ToUpper(ispPart[0]) + ispPart[1..];
            }
        }

        return null;
    }

    /// <summary>
    /// Check if an IP address is in a private range (RFC 1918 or CGNAT)
    /// </summary>
    private bool IsPrivateIp(string ip)
    {
        if (string.IsNullOrEmpty(ip))
            return true;

        var parts = ip.Split('.');
        if (parts.Length != 4)
            return true;

        if (!int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var second))
            return true;

        // 10.0.0.0/8
        if (first == 10)
            return true;

        // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
        if (first == 172 && second >= 16 && second <= 31)
            return true;

        // 192.168.0.0/16
        if (first == 192 && second == 168)
            return true;

        // 100.64.0.0/10 (CGNAT) - still useful for ping, but deprioritize
        // We'll accept CGNAT IPs since some ISPs like Starlink only give CGNAT
        // if (first == 100 && second >= 64 && second <= 127)
        //     return true;

        return false;
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

        // TODO(agent-infrastructure): Deploy SQM via agent when infrastructure is ready.
        // Requires: NetworkOptimizer.Agents package with SSH deployment capability.
        // Steps: 1) Generate scripts via NetworkOptimizer.Sqm.ScriptGenerator
        //        2) Push to gateway via agent SSH connection
        //        3) Verify tc qdisc installation and crontab entry

        await Task.Delay(2000); // Simulate deployment

        _currentConfig = config;

        return true;
    }

    public async Task<string> GenerateSqmScriptsAsync(SqmConfiguration config)
    {
        _logger.LogInformation("Generating SQM scripts for configuration: {@Config}", config);

        // TODO(sqm-scripts): Integrate NetworkOptimizer.Sqm.ScriptGenerator.
        // Requires: Finalized script templates for CAKE qdisc configuration.
        // Should generate: sqm-start.sh, sqm-stop.sh, crontab entry, tc-monitor.sh

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

    /// <summary>Suggested ISP gateway IP for ping monitoring (from mac_table)</summary>
    public string? SuggestedPingIp { get; set; }

    /// <summary>Whether UniFi Smart Queues (SQM) is enabled for this WAN in the controller</summary>
    public bool SmartqEnabled { get; set; }
}

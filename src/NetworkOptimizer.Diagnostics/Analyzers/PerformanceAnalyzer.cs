using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes network configuration for performance optimization opportunities:
/// hardware acceleration, jumbo frames, flow control, and cellular QoS.
/// </summary>
public class PerformanceAnalyzer
{
    private readonly DeviceTypeDetectionService _deviceTypeDetection;
    private readonly ILogger<PerformanceAnalyzer>? _logger;

    public PerformanceAnalyzer(
        DeviceTypeDetectionService deviceTypeDetection,
        ILogger<PerformanceAnalyzer>? logger = null)
    {
        _deviceTypeDetection = deviceTypeDetection;
        _logger = logger;
    }

    /// <summary>
    /// Run all performance checks.
    /// </summary>
    public List<PerformanceIssue> Analyze(
        List<UniFiDeviceResponse> devices,
        List<UniFiNetworkConfig> networks,
        List<UniFiClientResponse> clients,
        JsonDocument? settingsData,
        JsonDocument? qosRulesData,
        JsonDocument? wanEnrichedData = null,
        bool runPerformanceChecks = true,
        bool runCellularChecks = true)
    {
        var issues = new List<PerformanceIssue>();

        if (runPerformanceChecks)
        {
            issues.AddRange(CheckHardwareAcceleration(devices));
            issues.AddRange(CheckJumboFrames(devices, settingsData));
            issues.AddRange(CheckFlowControl(devices, networks, clients, settingsData));
        }

        if (runCellularChecks)
        {
            issues.AddRange(CheckCellularQos(devices, qosRulesData, wanEnrichedData));
        }

        return issues;
    }

    /// <summary>
    /// Check if hardware acceleration (packet offload) is enabled on the gateway.
    /// </summary>
    internal List<PerformanceIssue> CheckHardwareAcceleration(List<UniFiDeviceResponse> devices)
    {
        var issues = new List<PerformanceIssue>();

        var gateway = devices.FirstOrDefault(d => d.DeviceType == DeviceType.Gateway);
        if (gateway == null)
        {
            _logger?.LogDebug("No gateway found, skipping hardware acceleration check");
            return issues;
        }

        if (gateway.HardwareOffload == false)
        {
            issues.Add(new PerformanceIssue
            {
                Title = "Hardware Acceleration Disabled",
                Description = "Hardware Acceleration is disabled on your gateway. " +
                    "This means all traffic is processed by the CPU instead of being offloaded to dedicated hardware, " +
                    "which can reduce throughput and increase latency under load.",
                Recommendation = "Enable Hardware Acceleration in UniFi Devices > [your gateway] > Settings > Services. " +
                    "Some features like Smart Queues may auto-disable it, but newer firmware versions allow re-enabling it.",
                Severity = PerformanceSeverity.Info,
                Category = PerformanceCategory.Performance,
                DeviceName = gateway.Name
            });
        }

        return issues;
    }

    /// <summary>
    /// Check if jumbo frames should be enabled based on high-speed access ports.
    /// </summary>
    internal List<PerformanceIssue> CheckJumboFrames(
        List<UniFiDeviceResponse> devices,
        JsonDocument? settingsData)
    {
        var issues = new List<PerformanceIssue>();

        bool jumboEnabled = GetGlobalSwitchSetting(settingsData, "jumboframe_enabled");

        if (jumboEnabled)
        {
            _logger?.LogDebug("Jumbo frames already enabled, skipping check");
            return issues;
        }

        // Count access ports at 2.5 GbE or higher across all switches
        int highSpeedAccessPorts = CountHighSpeedAccessPorts(devices);

        if (highSpeedAccessPorts >= 2)
        {
            issues.Add(new PerformanceIssue
            {
                Title = "Jumbo Frames Not Enabled",
                Description = $"You have {highSpeedAccessPorts} access ports running at 2.5 GbE or higher, " +
                    "but jumbo frames are not enabled. Jumbo frames (MTU 9000) reduce per-packet overhead " +
                    "and can improve throughput by 10-30% for large transfers on high-speed links.",
                Recommendation = "Enable jumbo frames in UniFi Network Settings > Networks > Global Switch Settings (at the bottom). " +
                    "Ensure all devices on the path support jumbo frames to avoid fragmentation.",
                Severity = PerformanceSeverity.Info,
                Category = PerformanceCategory.Performance
            });
        }

        return issues;
    }

    /// <summary>
    /// Check if flow control should be enabled based on WAN speed and port mix.
    /// </summary>
    internal List<PerformanceIssue> CheckFlowControl(
        List<UniFiDeviceResponse> devices,
        List<UniFiNetworkConfig> networks,
        List<UniFiClientResponse> clients,
        JsonDocument? settingsData)
    {
        var issues = new List<PerformanceIssue>();

        bool flowCtrlEnabled = GetGlobalSwitchSetting(settingsData, "flowctrl_enabled");

        if (flowCtrlEnabled)
        {
            _logger?.LogDebug("Flow control already enabled, skipping check");
            return issues;
        }

        // Check condition 1: Fast WAN (> 800 Mbps download)
        bool hasFastWan = networks
            .Where(n => n.Purpose.Equals("wan", StringComparison.OrdinalIgnoreCase))
            .Any(n => n.WanProviderCapabilities?.DownloadMbps > 800);

        // Check condition 2: Mixed speed tiers + many WiFi user devices
        var accessPortSpeeds = GetAccessPortSpeedTiers(devices);
        bool hasMixedSpeeds = accessPortSpeeds.Count >= 2;

        int wifiUserDeviceCount = 0;
        if (hasMixedSpeeds)
        {
            wifiUserDeviceCount = CountWirelessUserDevices(clients);
        }

        bool hasManyWifiDevices = wifiUserDeviceCount >= 10;
        bool mixedSpeedCondition = hasMixedSpeeds && hasManyWifiDevices;

        if (!hasFastWan && !mixedSpeedCondition)
        {
            return issues;
        }

        // Build recommendation based on which conditions triggered
        string description;
        if (hasFastWan && mixedSpeedCondition)
        {
            description = "Your network has a fast WAN connection (> 800 Mbps) and mixed-speed switch ports " +
                $"with {wifiUserDeviceCount} wireless user devices. Flow control helps prevent packet loss " +
                "when faster ports overwhelm slower ones during bursts.";
        }
        else if (hasFastWan)
        {
            description = "Your WAN connection exceeds 800 Mbps. Flow control helps prevent packet loss " +
                "at speed boundaries between your WAN and LAN during traffic bursts.";
        }
        else
        {
            var speedList = string.Join(", ", accessPortSpeeds.OrderBy(s => s).Select(s => $"{s} Mbps"));
            description = $"Your network has mixed port speeds ({speedList}) and {wifiUserDeviceCount} " +
                "wireless user devices. Flow control helps prevent packet loss when faster ports send " +
                "to slower ones during bursts.";
        }

        issues.Add(new PerformanceIssue
        {
            Title = "Flow Control Not Enabled",
            Description = description,
            Recommendation = "Enable flow control in UniFi Network Settings > Internet (at the bottom). " +
                "This uses IEEE 802.3x pause frames to prevent buffer overflows between ports.",
            Severity = PerformanceSeverity.Info,
            Category = PerformanceCategory.Performance
        });

        return issues;
    }

    /// <summary>
    /// Check if cellular WAN is present and QoS rules cover bandwidth-heavy app categories.
    /// </summary>
    internal List<PerformanceIssue> CheckCellularQos(
        List<UniFiDeviceResponse> devices,
        JsonDocument? qosRulesData,
        JsonDocument? wanEnrichedData)
    {
        var issues = new List<PerformanceIssue>();

        var gateway = devices.FirstOrDefault(d => d.DeviceType == DeviceType.Gateway);
        if (gateway == null)
        {
            return issues;
        }

        var wanInterfaces = gateway.GetWanInterfaces();
        var cellularWan = wanInterfaces.FirstOrDefault(w => w.IsCellular);

        if (cellularWan == null)
        {
            _logger?.LogDebug("No cellular WAN detected, skipping QoS check");
            return issues;
        }

        // Determine if the cellular WAN is failover-only or in the load balancing mix.
        // Failover = more likely to have a small data plan, so Recommendation severity.
        // Load balanced = actively used, so also Recommendation but different description.
        bool isFailover = IsCellularFailover(cellularWan, wanEnrichedData);
        _logger?.LogInformation("Cellular WAN detected ({Key}, type={Type}, failover={Failover}), checking QoS rule coverage",
            cellularWan.Key, cellularWan.Type, isFailover);

        // If cellular is failover-only, severity is Info (less urgent since it's not primary)
        // If cellular is in the load balancing mix, severity is Recommendation (actively burning data)
        var severity = isFailover ? PerformanceSeverity.Info : PerformanceSeverity.Recommendation;

        string cellularContext = isFailover
            ? "Your cellular WAN is configured as failover"
            : "Your cellular WAN is in the load balancing mix";

        // Parse existing QoS rules to find which app categories are covered
        var coveredCategories = GetCoveredCategories(qosRulesData, _logger);

        _logger?.LogDebug("QoS coverage: {Categories}", coveredCategories.Count > 0
            ? string.Join(", ", coveredCategories)
            : "none");

        // Check each category
        if (!coveredCategories.Contains("streaming"))
        {
            var examples = GetTopAppNames(StreamingAppIds.StreamingVideo, 4);
            issues.Add(new PerformanceIssue
            {
                Title = "Streaming Video Not Rate-Limited",
                Description = $"{cellularContext}, but streaming video apps " +
                    $"({examples}) don't have bandwidth limits. Streaming can quickly exhaust cellular data caps.",
                Recommendation = "Create a QoS Rule under Policy Engine > Policy Table > QoS Rules to limit " +
                    "streaming video apps when on cellular. Consider setting a bandwidth cap per client.",
                Severity = severity,
                Category = PerformanceCategory.CellularDataSavings,
                DeviceName = gateway.Name
            });
        }

        if (!coveredCategories.Contains("cloud"))
        {
            var examples = GetTopAppNames(StreamingAppIds.CloudStorage, 4);
            issues.Add(new PerformanceIssue
            {
                Title = "Cloud Sync Not Rate-Limited",
                Description = $"{cellularContext}, but cloud storage apps " +
                    $"({examples}) don't have bandwidth limits. Background sync can consume significant data.",
                Recommendation = "Create a QoS Rule under Policy Engine > Policy Table > QoS Rules to limit cloud storage sync speed when on cellular. " +
                    "This prevents large uploads/downloads from burning through your data plan.",
                Severity = severity,
                Category = PerformanceCategory.CellularDataSavings,
                DeviceName = gateway.Name
            });
        }

        if (!coveredCategories.Contains("downloads"))
        {
            var examples = GetTopAppNames(StreamingAppIds.LargeDownloads, 3);
            issues.Add(new PerformanceIssue
            {
                Title = "Game/App Downloads Not Rate-Limited",
                Description = $"{cellularContext}, but game stores and large download " +
                    $"platforms ({examples}) don't have bandwidth limits. A single game update can be 50+ GB.",
                Recommendation = "Create a QoS Rule under Policy Engine > Policy Table > QoS Rules to limit or block game/app downloads when on cellular. " +
                    "Game updates alone can exceed monthly data caps in a single download.",
                Severity = severity,
                Category = PerformanceCategory.CellularDataSavings,
                DeviceName = gateway.Name
            });
        }

        return issues;
    }

    #region Helper Methods

    /// <summary>
    /// Determines if the cellular WAN is configured as failover-only (not load balanced).
    /// Uses the enriched WAN config (v2 API) which has wan_load_balance_type and wan_networkgroup.
    /// Matches the cellular WAN interface key (e.g., "wan3") to wan_networkgroup (e.g., "WAN3").
    /// </summary>
    internal static bool IsCellularFailover(GatewayWanInterface cellularWan, JsonDocument? wanEnrichedData)
    {
        if (wanEnrichedData == null)
            return true; // Can't determine, assume failover (more conservative)

        // The enriched config is an array of objects with a "configuration" property
        JsonElement configArray;
        if (wanEnrichedData.RootElement.ValueKind == JsonValueKind.Array)
        {
            configArray = wanEnrichedData.RootElement;
        }
        else if (wanEnrichedData.RootElement.TryGetProperty("data", out var data) &&
                 data.ValueKind == JsonValueKind.Array)
        {
            configArray = data;
        }
        else
        {
            return true;
        }

        foreach (var entry in configArray.EnumerateArray())
        {
            if (!entry.TryGetProperty("configuration", out var config))
                continue;

            // Match wan_networkgroup (e.g., "WAN3") to the interface key (e.g., "wan3")
            if (!config.TryGetProperty("wan_networkgroup", out var networkGroup))
                continue;

            if (!networkGroup.GetString()?.Equals(cellularWan.Key, StringComparison.OrdinalIgnoreCase) == true)
                continue;

            // Found the matching WAN config - check load balance type
            if (config.TryGetProperty("wan_load_balance_type", out var lbType))
            {
                return lbType.GetString()?.Equals("failover-only", StringComparison.OrdinalIgnoreCase) == true;
            }
        }

        return true; // Can't determine, assume failover
    }

    /// <summary>
    /// Reads a boolean property from the global_switch settings object.
    /// </summary>
    internal static bool GetGlobalSwitchSetting(JsonDocument? settingsData, string propertyName)
    {
        if (settingsData == null)
            return false;

        if (!settingsData.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("key", out var key) &&
                key.GetString() == "global_switch" &&
                item.TryGetProperty(propertyName, out var value))
            {
                return value.ValueKind == JsonValueKind.True;
            }
        }

        return false;
    }

    /// <summary>
    /// Count access ports (non-uplink, non-WAN, up, speed > 0) at 2.5 GbE or higher.
    /// </summary>
    internal static int CountHighSpeedAccessPorts(List<UniFiDeviceResponse> devices)
    {
        int count = 0;

        foreach (var device in devices)
        {
            if (device.PortTable == null)
                continue;

            foreach (var port in device.PortTable)
            {
                if (IsAccessPort(port) && port.Speed >= 2500)
                    count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Get the set of distinct speed tiers among active access ports.
    /// </summary>
    internal static HashSet<int> GetAccessPortSpeedTiers(List<UniFiDeviceResponse> devices)
    {
        var speeds = new HashSet<int>();

        foreach (var device in devices)
        {
            if (device.PortTable == null)
                continue;

            foreach (var port in device.PortTable)
            {
                if (IsAccessPort(port))
                    speeds.Add(port.Speed);
            }
        }

        return speeds;
    }

    /// <summary>
    /// Whether a switch port is an "access port" (non-uplink, non-WAN, active).
    /// </summary>
    private static bool IsAccessPort(SwitchPort port)
    {
        return !port.IsUplink &&
               port.Up &&
               port.Speed > 0 &&
               !(port.NetworkName?.StartsWith("wan", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Count wireless client devices that are user devices (phones, laptops, tablets).
    /// </summary>
    private int CountWirelessUserDevices(List<UniFiClientResponse> clients)
    {
        int count = 0;

        foreach (var client in clients)
        {
            if (!client.IsWired)
            {
                var detection = _deviceTypeDetection.DetectDeviceType(client);
                var category = detection.Category;

                if (category == ClientDeviceCategory.Smartphone ||
                    category == ClientDeviceCategory.Laptop ||
                    category == ClientDeviceCategory.Tablet ||
                    category == ClientDeviceCategory.Desktop)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Parse QoS rules to determine which app categories are covered by limiting rules.
    /// </summary>
    internal static HashSet<string> GetCoveredCategories(JsonDocument? qosRulesData, ILogger? logger = null)
    {
        var covered = new HashSet<string>();

        if (qosRulesData == null)
        {
            logger?.LogDebug("QoS rules data is null");
            return covered;
        }

        // QoS rules response can be either a flat array or wrapped in a data property
        JsonElement rulesArray;
        if (qosRulesData.RootElement.ValueKind == JsonValueKind.Array)
        {
            rulesArray = qosRulesData.RootElement;
        }
        else if (qosRulesData.RootElement.TryGetProperty("data", out var data) &&
                 data.ValueKind == JsonValueKind.Array)
        {
            rulesArray = data;
        }
        else
        {
            logger?.LogDebug("QoS rules: unexpected JSON structure (kind={Kind})", qosRulesData.RootElement.ValueKind);
            return covered;
        }

        int ruleCount = 0;
        foreach (var _ in rulesArray.EnumerateArray()) ruleCount++;
        logger?.LogDebug("QoS rules: found {Count} rules total", ruleCount);

        // Collect all app IDs targeted by enabled limiting rules
        var targetedAppIds = new HashSet<int>();

        foreach (var rule in rulesArray.EnumerateArray())
        {
            string? ruleName = rule.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

            // Must be enabled
            if (!rule.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean())
            {
                logger?.LogDebug("QoS rule '{Name}': skipped (disabled)", ruleName ?? "unnamed");
                continue;
            }

            // Must be a limiting rule
            string? objectiveStr = rule.TryGetProperty("objective", out var objective) ? objective.GetString() : null;
            if (objectiveStr?.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) != true)
            {
                logger?.LogDebug("QoS rule '{Name}': skipped (objective={Objective})", ruleName ?? "unnamed", objectiveStr ?? "null");
                continue;
            }

            // Collect app IDs from destination
            if (rule.TryGetProperty("destination", out var destination) &&
                destination.TryGetProperty("app_ids", out var appIds) &&
                appIds.ValueKind == JsonValueKind.Array)
            {
                var ruleAppIds = new List<int>();
                foreach (var appId in appIds.EnumerateArray())
                {
                    if (appId.TryGetInt32(out int id))
                    {
                        targetedAppIds.Add(id);
                        ruleAppIds.Add(id);
                    }
                }
                logger?.LogDebug("QoS rule '{Name}': LIMIT with {Count} app IDs: [{Ids}]",
                    ruleName ?? "unnamed", ruleAppIds.Count, string.Join(", ", ruleAppIds));
            }
            else
            {
                logger?.LogDebug("QoS rule '{Name}': LIMIT but no destination.app_ids found", ruleName ?? "unnamed");
            }
        }

        logger?.LogDebug("QoS: {Count} total targeted app IDs across all LIMIT rules", targetedAppIds.Count);

        // Check coverage for each category (per-category thresholds)
        int streamingHits = targetedAppIds.Count(id => StreamingAppIds.StreamingVideo.Contains(id));
        logger?.LogDebug("QoS coverage: streaming {Hits}/{Total} (need {Min})",
            streamingHits, StreamingAppIds.StreamingVideo.Count, StreamingAppIds.MinStreamingForCoverage);
        if (streamingHits >= StreamingAppIds.MinStreamingForCoverage)
            covered.Add("streaming");

        int cloudHits = targetedAppIds.Count(id => StreamingAppIds.CloudStorage.Contains(id));
        logger?.LogDebug("QoS coverage: cloud {Hits}/{Total} (need {Min})",
            cloudHits, StreamingAppIds.CloudStorage.Count, StreamingAppIds.MinCloudForCoverage);
        if (cloudHits >= StreamingAppIds.MinCloudForCoverage)
            covered.Add("cloud");

        int downloadHits = targetedAppIds.Count(id => StreamingAppIds.LargeDownloads.Contains(id));
        logger?.LogDebug("QoS coverage: downloads {Hits}/{Total} (need {Min})",
            downloadHits, StreamingAppIds.LargeDownloads.Count, StreamingAppIds.MinDownloadsForCoverage);
        if (downloadHits >= StreamingAppIds.MinDownloadsForCoverage)
            covered.Add("downloads");

        return covered;
    }

    /// <summary>
    /// Get top N app names from a set for display in recommendations.
    /// </summary>
    private static string GetTopAppNames(HashSet<int> appIds, int count)
    {
        var names = appIds
            .Take(count)
            .Select(id => StreamingAppIds.AppNames.TryGetValue(id, out var name) ? name : id.ToString())
            .ToList();

        return string.Join(", ", names);
    }

    #endregion
}

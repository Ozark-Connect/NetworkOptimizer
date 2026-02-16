using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes network configuration for performance optimization opportunities:
/// Hardware Acceleration, Jumbo Frames, Flow Control, and cellular QoS.
/// </summary>
public class PerformanceAnalyzer
{
    private readonly DeviceTypeDetectionService _deviceTypeDetection;
    private readonly ILogger<PerformanceAnalyzer>? _logger;

    /// <summary>
    /// Set after Analyze() runs - indicates whether a cellular WAN was detected.
    /// </summary>
    public bool CellularWanDetected { get; private set; }

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
        CellularWanDetected = false;
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
    /// Check if Hardware Acceleration (packet offload) is enabled on the gateway.
    /// </summary>
    internal List<PerformanceIssue> CheckHardwareAcceleration(List<UniFiDeviceResponse> devices)
    {
        var issues = new List<PerformanceIssue>();

        var gateway = devices.FirstOrDefault(d => d.DeviceType == DeviceType.Gateway);
        if (gateway == null)
        {
            _logger?.LogDebug("No gateway found, skipping Hardware Acceleration check");
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
    /// Check Jumbo Frames configuration using exclusion-aware global switch settings.
    /// Three scenarios: global off (suggest enabling), global on with excluded device off (mismatch),
    /// global off but all excluded devices on (suggest using global).
    /// </summary>
    internal List<PerformanceIssue> CheckJumboFrames(
        List<UniFiDeviceResponse> devices,
        JsonDocument? settingsData)
    {
        var issues = new List<PerformanceIssue>();
        var settings = GlobalSwitchSettings.FromSettingsJson(settingsData);
        bool globalJumbo = settings?.JumboFramesEnabled ?? false;

        // Find excluded devices and their Jumbo Frames status
        var excludedDevices = GetExcludedDevicesWithSetting(devices, settings,
            d => settings?.GetEffectiveJumboFrames(d) ?? false);

        if (globalJumbo)
        {
            // Scenario 2: Global ON, check for excluded devices
            foreach (var (device, effectiveValue) in excludedDevices)
            {
                if (!effectiveValue)
                {
                    // Excluded device has Jumbo OFF - MTU mismatch
                    issues.Add(new PerformanceIssue
                    {
                        Title = $"Jumbo Frames Disabled on {device.Name}",
                        Description = $"Jumbo Frames are enabled globally, but {device.Name} is using device-specific " +
                            "settings with Jumbo Frames disabled. This creates an MTU mismatch that can cause " +
                            "fragmentation and reduced throughput on paths through this device.",
                        Recommendation = "Enable Global Switch Settings on this device in UniFi Devices > " +
                            $"{device.Name}, or enable Jumbo Frames in its device-specific settings.",
                        Severity = PerformanceSeverity.Recommendation,
                        Category = PerformanceCategory.Performance,
                        DeviceName = device.Name
                    });
                }
                else
                {
                    // Excluded device has Jumbo ON but not inheriting global - suggest absorbing
                    issues.Add(new PerformanceIssue
                    {
                        Title = $"Jumbo Frames Set Per-Device on {device.Name}",
                        Description = $"Jumbo Frames are enabled both globally and on {device.Name}, but {device.Name} " +
                            "is using device-specific settings instead of inheriting from Global Switch Settings. " +
                            "If the global setting changes, this device won't follow.",
                        Recommendation = $"Enable Global Switch Settings on {device.Name} in UniFi Devices > " +
                            $"{device.Name} so it automatically inherits global settings.",
                        Severity = PerformanceSeverity.Info,
                        Category = PerformanceCategory.Performance,
                        DeviceName = device.Name
                    });
                }
            }
        }
        else
        {
            // Global OFF - check scenarios 1 and 3
            var excludedWithJumboOn = excludedDevices.Where(e => e.EffectiveValue).ToList();
            var excludedWithJumboOff = excludedDevices.Where(e => !e.EffectiveValue).ToList();

            if (excludedDevices.Count > 0 && excludedWithJumboOff.Count == 0 && excludedWithJumboOn.Count > 0)
            {
                // Scenario 3: Global OFF, all excluded devices have it ON
                issues.Add(new PerformanceIssue
                {
                    Title = "Jumbo Frames Set Per-Device",
                    Description = "Jumbo Frames are enabled on all your devices individually, but the global switch " +
                        "setting is off. If a new device is added, it won't have Jumbo Frames unless manually configured.",
                    Recommendation = "Consider enabling Jumbo Frames in UniFi Network Settings > Networks > " +
                        "Global Switch Settings (at the bottom) for consistent coverage across all current and future devices.",
                    Severity = PerformanceSeverity.Info,
                    Category = PerformanceCategory.Performance
                });
            }
            else
            {
                // Scenario 1: Global OFF, not all devices have it
                int highSpeedAccessPorts = CountHighSpeedAccessPorts(devices);

                if (highSpeedAccessPorts >= 2)
                {
                    string description;
                    PerformanceSeverity severity;

                    if (excludedWithJumboOn.Count > 0)
                    {
                        var deviceNames = string.Join(", ", excludedWithJumboOn.Select(e => e.Device.Name));
                        description = $"You have {highSpeedAccessPorts} access ports running at 2.5 GbE or higher. " +
                            $"Jumbo Frames are enabled on {deviceNames} but not on the remaining devices, " +
                            "creating an MTU mismatch that can cause fragmentation.";
                        severity = PerformanceSeverity.Recommendation;
                    }
                    else
                    {
                        description = $"You have {highSpeedAccessPorts} access ports running at 2.5 GbE or higher, " +
                            "but Jumbo Frames are not enabled. Jumbo Frames (MTU 9000) reduce per-packet overhead " +
                            "and can improve throughput by 10-30% for large transfers on high-speed links.";
                        severity = PerformanceSeverity.Info;
                    }

                    issues.Add(new PerformanceIssue
                    {
                        Title = "Jumbo Frames Not Enabled",
                        Description = description,
                        Recommendation = "Enable Jumbo Frames in UniFi Network Settings > Networks > Global Switch Settings (at the bottom). " +
                            "Ensure all devices on the path support Jumbo Frames to avoid fragmentation.",
                        Severity = severity,
                        Category = PerformanceCategory.Performance
                    });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Check Flow Control configuration using exclusion-aware global switch settings.
    /// Three scenarios: global off (suggest enabling), global on with excluded device off (mismatch),
    /// global off but all excluded devices on (suggest using global).
    /// </summary>
    internal List<PerformanceIssue> CheckFlowControl(
        List<UniFiDeviceResponse> devices,
        List<UniFiNetworkConfig> networks,
        List<UniFiClientResponse> clients,
        JsonDocument? settingsData)
    {
        var issues = new List<PerformanceIssue>();
        var settings = GlobalSwitchSettings.FromSettingsJson(settingsData);
        bool globalFlowCtrl = settings?.FlowControlEnabled ?? false;

        // Find excluded devices and their Flow Control status
        // Exclude gateways - Flow Control is a switch-only feature in UniFi
        var excludedDevices = GetExcludedDevicesWithSetting(devices, settings,
            d => settings?.GetEffectiveFlowControl(d) ?? false)
            .Where(e => e.Device.DeviceType != DeviceType.Gateway)
            .ToList();

        if (globalFlowCtrl)
        {
            // Scenario 2: Global ON, check for excluded devices
            foreach (var (device, effectiveValue) in excludedDevices)
            {
                if (!effectiveValue)
                {
                    // Excluded device has Flow Control OFF - mismatch
                    issues.Add(new PerformanceIssue
                    {
                        Title = $"Flow Control Disabled on {device.Name}",
                        Description = $"Flow Control is enabled globally, but {device.Name} is using device-specific " +
                            "settings with Flow Control disabled. This means this device won't send or respond to " +
                            "pause frames, which can lead to packet loss during traffic bursts.",
                        Recommendation = "Enable Global Switch Settings on this device in UniFi Devices > " +
                            $"{device.Name}, or enable Flow Control in its device-specific settings.",
                        Severity = PerformanceSeverity.Recommendation,
                        Category = PerformanceCategory.Performance,
                        DeviceName = device.Name
                    });
                }
                else
                {
                    // Excluded device has Flow Control ON but not inheriting global - suggest absorbing
                    issues.Add(new PerformanceIssue
                    {
                        Title = $"Flow Control Set Per-Device on {device.Name}",
                        Description = $"Flow Control is enabled both globally and on {device.Name}, but {device.Name} " +
                            "is using device-specific settings instead of inheriting from Global Switch Settings. " +
                            "If the global setting changes, this device won't follow.",
                        Recommendation = $"Enable Global Switch Settings on {device.Name} in UniFi Devices > " +
                            $"{device.Name} so it automatically inherits global settings.",
                        Severity = PerformanceSeverity.Info,
                        Category = PerformanceCategory.Performance,
                        DeviceName = device.Name
                    });
                }
            }
        }
        else
        {
            // Global OFF - check scenarios 1 and 3
            var excludedWithFlowCtrlOn = excludedDevices.Where(e => e.EffectiveValue).ToList();
            var excludedWithFlowCtrlOff = excludedDevices.Where(e => !e.EffectiveValue).ToList();

            if (excludedDevices.Count > 0 && excludedWithFlowCtrlOff.Count == 0 && excludedWithFlowCtrlOn.Count > 0)
            {
                // Scenario 3: Global OFF, all excluded devices have it ON
                issues.Add(new PerformanceIssue
                {
                    Title = "Flow Control Set Per-Device",
                    Description = "Flow Control is enabled on all your devices individually, but the global switch " +
                        "setting is off. New devices won't have Flow Control unless manually configured.",
                    Recommendation = "Consider enabling Flow Control in UniFi Network Settings > Internet (at the bottom) " +
                        "for consistent coverage.",
                    Severity = PerformanceSeverity.Info,
                    Category = PerformanceCategory.Performance
                });
            }
            else
            {
                // Scenario 1: Global OFF, not all devices have it
                // Check triggering conditions: fast WAN or mixed speeds + WiFi devices
                bool hasFastWan = networks
                    .Where(n => n.Purpose.Equals("wan", StringComparison.OrdinalIgnoreCase))
                    .Any(n => n.WanProviderCapabilities?.DownloadMbps > 800);

                var accessPortSpeeds = GetAccessPortSpeedTiers(devices);
                bool hasMixedSpeeds = accessPortSpeeds.Count >= 2;

                int wifiUserDeviceCount = 0;
                if (hasMixedSpeeds)
                    wifiUserDeviceCount = CountWirelessUserDevices(clients);

                bool mixedSpeedCondition = hasMixedSpeeds && wifiUserDeviceCount >= 10;

                if (!hasFastWan && !mixedSpeedCondition)
                    return issues;

                string description;
                PerformanceSeverity severity;

                if (excludedWithFlowCtrlOn.Count > 0)
                {
                    var deviceNames = string.Join(", ", excludedWithFlowCtrlOn.Select(e => e.Device.Name));
                    severity = PerformanceSeverity.Recommendation;

                    if (hasFastWan && mixedSpeedCondition)
                    {
                        description = "Your network has a fast WAN connection (> 800 Mbps) and mixed-speed switch ports " +
                            $"with {wifiUserDeviceCount} wireless user devices. Flow Control is enabled on {deviceNames} " +
                            "but not on the remaining devices, creating inconsistent burst handling.";
                    }
                    else if (hasFastWan)
                    {
                        description = $"Your WAN speed exceeds 800 Mbps. Flow Control is enabled on {deviceNames} " +
                            "but not on the remaining devices.";
                    }
                    else
                    {
                        var speedList = string.Join(", ", accessPortSpeeds.OrderBy(s => s).Select(s => $"{s} Mbps"));
                        description = $"Your network has mixed port speeds ({speedList}) and {wifiUserDeviceCount} " +
                            $"wireless user devices. Flow Control is enabled on {deviceNames} but not on the remaining devices.";
                    }
                }
                else
                {
                    severity = PerformanceSeverity.Info;

                    if (hasFastWan && mixedSpeedCondition)
                    {
                        description = "Your network has a fast WAN connection (> 800 Mbps) and mixed-speed switch ports " +
                            $"with {wifiUserDeviceCount} wireless user devices. Flow Control helps prevent packet loss " +
                            "when faster ports overwhelm slower ones during bursts.";
                    }
                    else if (hasFastWan)
                    {
                        description = "Your WAN speed exceeds 800 Mbps. Enabling Flow Control can help prevent packet loss " +
                            "during traffic bursts when your gateway receives data faster than it can forward to slower LAN devices. " +
                            "This is most beneficial with multi-gigabit WAN connections (1.5+ Gbps).";
                    }
                    else
                    {
                        var speedList = string.Join(", ", accessPortSpeeds.OrderBy(s => s).Select(s => $"{s} Mbps"));
                        description = $"Your network has mixed port speeds ({speedList}) and {wifiUserDeviceCount} " +
                            "wireless user devices. Flow Control helps prevent packet loss when faster ports send " +
                            "to slower ones during bursts.";
                    }
                }

                issues.Add(new PerformanceIssue
                {
                    Title = "Flow Control Not Enabled",
                    Description = description,
                    Recommendation = "If you are noticing internet performance deficiency on certain devices, " +
                        "consider enabling Flow Control in UniFi Network Settings > Internet (at the bottom).",
                    Severity = severity,
                    Category = PerformanceCategory.Performance
                });
            }
        }

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

        CellularWanDetected = true;

        // Check WAN failover mode from enriched config
        bool isFailover = IsCellularFailover(cellularWan, wanEnrichedData);

        // Check data limits from the modem device (umbb)
        var modem = devices.FirstOrDefault(d =>
            d.Type.Equals("umbb", StringComparison.OrdinalIgnoreCase));
        var dataLimit = GetModemDataLimit(modem);
        bool hasSmallDataPlan = dataLimit.Enabled && dataLimit.Bytes < 500L * 1024 * 1024 * 1024;

        _logger?.LogInformation(
            "Cellular WAN detected ({Key}, type={Type}, failover={Failover}, dataLimit={Limit}), checking QoS rule coverage",
            cellularWan.Key, cellularWan.Type, isFailover,
            dataLimit.Enabled ? $"{dataLimit.Bytes / (1024 * 1024 * 1024)} GB" : "none");

        // Failover or small data plan → Recommendation (more urgent)
        // Load balanced with large/no data cap → Info
        var severity = (isFailover || hasSmallDataPlan)
            ? PerformanceSeverity.Recommendation
            : PerformanceSeverity.Info;

        string cellularContext = isFailover
            ? "Your cellular WAN is configured as failover"
            : "Your cellular WAN is in the load balancing mix";

        // Get the cellular WAN's network config _id so we only count QoS rules assigned to it
        string? cellularWanConfigId = GetCellularWanConfigId(cellularWan, wanEnrichedData);
        _logger?.LogDebug("Cellular WAN config ID: {Id}", cellularWanConfigId ?? "not found");

        // Parse existing QoS rules to find which apps are targeted by LIMIT rules on the cellular WAN
        var targetedAppIds = GetTargetedAppIds(qosRulesData, cellularWanConfigId, _logger);

        // Check each category - build context-aware descriptions showing partial coverage
        var streamingGap = BuildCategoryGapDescription(cellularContext, targetedAppIds,
            StreamingAppIds.StreamingVideo, StreamingAppIds.MinStreamingForCoverage, "streaming video apps");
        if (streamingGap != null)
        {
            issues.Add(new PerformanceIssue
            {
                Title = "Streaming Video Not Rate-Limited",
                Description = streamingGap,
                Recommendation = "Create a QoS Rule under Policy Engine > Policy Table > QoS Rules to limit " +
                    "streaming video apps when on cellular. " +
                    "<br><a href=\"https://ozarkconnect.net/blog/unifi-5g-backup-qos\" target=\"_blank\">How-To Guide</a>",
                Severity = severity,
                Category = PerformanceCategory.CellularDataSavings,
                DeviceName = gateway.Name
            });
        }

        var cloudGap = BuildCategoryGapDescription(cellularContext, targetedAppIds,
            StreamingAppIds.CloudStorage, StreamingAppIds.MinCloudForCoverage, "cloud storage apps");
        if (cloudGap != null)
        {
            issues.Add(new PerformanceIssue
            {
                Title = "Cloud Sync Not Rate-Limited",
                Description = cloudGap,
                Recommendation = "Create a QoS Rule under Policy Engine > Policy Table > QoS Rules to limit cloud storage sync speed when on cellular. " +
                    "This prevents large uploads/downloads from burning through your data plan. " +
                    "<br><a href=\"https://ozarkconnect.net/blog/unifi-5g-backup-qos\" target=\"_blank\">How-To Guide</a>",
                Severity = severity,
                Category = PerformanceCategory.CellularDataSavings,
                DeviceName = gateway.Name
            });
        }

        var downloadGap = BuildCategoryGapDescription(cellularContext, targetedAppIds,
            StreamingAppIds.LargeDownloads, StreamingAppIds.MinDownloadsForCoverage, "game stores and large download platforms");
        if (downloadGap != null)
        {
            issues.Add(new PerformanceIssue
            {
                Title = "Game/App Downloads Not Rate-Limited",
                Description = downloadGap,
                Recommendation = "Create a QoS Rule under Policy Engine > Policy Table > QoS Rules to limit or block game/app downloads when on cellular. " +
                    "Game updates alone can exceed monthly data caps in a single download. " +
                    "<br><a href=\"https://ozarkconnect.net/blog/unifi-5g-backup-qos\" target=\"_blank\">How-To Guide</a>",
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

            if (networkGroup.GetString()?.Equals(cellularWan.Key, StringComparison.OrdinalIgnoreCase) != true)
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
    /// Extracts data limit info from the cellular modem's mbb_overrides.
    /// Uses the primary SIM slot's data_limit_enabled and data_soft_limit_bytes.
    /// </summary>
    internal static (bool Enabled, long Bytes) GetModemDataLimit(UniFiDeviceResponse? modem)
    {
        if (modem?.AdditionalData == null)
            return (false, 0);

        if (!modem.AdditionalData.TryGetValue("mbb_overrides", out var overridesEl))
            return (false, 0);

        if (!overridesEl.TryGetProperty("sim", out var simArray) ||
            simArray.ValueKind != JsonValueKind.Array)
            return (false, 0);

        // Find the primary slot, or use slot 1 as default
        int primarySlot = 1;
        if (overridesEl.TryGetProperty("primary_slot", out var primaryEl) &&
            primaryEl.TryGetInt32(out int ps))
        {
            primarySlot = ps;
        }

        foreach (var sim in simArray.EnumerateArray())
        {
            int slot = sim.TryGetProperty("slot", out var slotEl) && slotEl.TryGetInt32(out int s) ? s : 0;
            if (slot != primarySlot)
                continue;

            bool enabled = sim.TryGetProperty("data_limit_enabled", out var dle) && dle.GetBoolean();
            long bytes = sim.TryGetProperty("data_soft_limit_bytes", out var dsl) && dsl.TryGetInt64(out long b) ? b : 0;

            return (enabled, bytes);
        }

        return (false, 0);
    }

    /// <summary>
    /// Get excluded devices and their effective setting value.
    /// Returns only devices that are in the switch_exclusions list.
    /// </summary>
    internal static List<(UniFiDeviceResponse Device, bool EffectiveValue)> GetExcludedDevicesWithSetting(
        List<UniFiDeviceResponse> devices,
        GlobalSwitchSettings? settings,
        Func<UniFiDeviceResponse, bool> getEffectiveValue)
    {
        if (settings == null)
            return new List<(UniFiDeviceResponse, bool)>();

        return devices
            .Where(d => !string.IsNullOrEmpty(d.Mac) && settings.IsExcluded(d.Mac))
            .Select(d => (Device: d, EffectiveValue: getEffectiveValue(d)))
            .ToList();
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
    /// Extracts the cellular WAN's network config _id from the enriched WAN config.
    /// Matches the cellular WAN interface key (e.g., "wan3") to wan_networkgroup (e.g., "WAN3").
    /// </summary>
    internal static string? GetCellularWanConfigId(GatewayWanInterface cellularWan, JsonDocument? wanEnrichedData)
    {
        if (wanEnrichedData == null)
            return null;

        JsonElement configArray;
        if (wanEnrichedData.RootElement.ValueKind == JsonValueKind.Array)
            configArray = wanEnrichedData.RootElement;
        else if (wanEnrichedData.RootElement.TryGetProperty("data", out var data) &&
                 data.ValueKind == JsonValueKind.Array)
            configArray = data;
        else
            return null;

        foreach (var entry in configArray.EnumerateArray())
        {
            if (!entry.TryGetProperty("configuration", out var config))
                continue;

            if (!config.TryGetProperty("wan_networkgroup", out var networkGroup))
                continue;

            if (networkGroup.GetString()?.Equals(cellularWan.Key, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            // Found the matching WAN config - return its _id
            if (config.TryGetProperty("_id", out var idProp))
                return idProp.GetString();
        }

        return null;
    }

    /// <summary>
    /// Parse QoS rules and return all app IDs targeted by enabled LIMIT rules
    /// that are assigned to the specified WAN network.
    /// </summary>
    internal static HashSet<int> GetTargetedAppIds(JsonDocument? qosRulesData, string? cellularWanConfigId, ILogger? logger = null)
    {
        var targetedAppIds = new HashSet<int>();

        if (qosRulesData == null)
        {
            logger?.LogDebug("QoS rules data is null");
            return targetedAppIds;
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
            return targetedAppIds;
        }

        int ruleCount = 0;
        foreach (var _ in rulesArray.EnumerateArray()) ruleCount++;
        logger?.LogDebug("QoS rules: found {Count} rules total", ruleCount);

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

            // Must be assigned to the cellular WAN (has wan_or_vpn_network matching the cellular config ID)
            if (cellularWanConfigId != null)
            {
                string? ruleWan = rule.TryGetProperty("wan_or_vpn_network", out var wanProp) ? wanProp.GetString() : null;
                if (ruleWan == null || !ruleWan.Equals(cellularWanConfigId, StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("QoS rule '{Name}': skipped (wan_or_vpn_network={Wan}, need {Need})",
                        ruleName ?? "unnamed", ruleWan ?? "none", cellularWanConfigId);
                    continue;
                }
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

        logger?.LogDebug("QoS: {Count} total targeted app IDs across cellular WAN LIMIT rules", targetedAppIds.Count);
        return targetedAppIds;
    }

    /// <summary>
    /// Check category coverage and build a description with context about partial coverage.
    /// Returns null if the category is fully covered, otherwise returns a description string.
    /// </summary>
    private static string? BuildCategoryGapDescription(
        string cellularContext, HashSet<int> targetedAppIds,
        HashSet<int> categoryAppIds, int minForCoverage, string categoryLabel)
    {
        var coveredIds = categoryAppIds.Where(id => targetedAppIds.Contains(id)).ToList();
        var uncoveredIds = categoryAppIds.Where(id => !targetedAppIds.Contains(id)).ToList();

        if (coveredIds.Count >= minForCoverage)
            return null; // Fully covered

        if (coveredIds.Count > 0)
        {
            // Partial coverage - show what's covered and what's missing
            var coveredNames = coveredIds
                .Select(id => StreamingAppIds.AppNames.TryGetValue(id, out var n) ? n : null)
                .Where(n => n != null).ToList();
            var uncoveredNames = uncoveredIds
                .Take(4)
                .Select(id => StreamingAppIds.AppNames.TryGetValue(id, out var n) ? n : id.ToString())
                .ToList();

            return $"{cellularContext}. Your QoS rules cover {string.Join(", ", coveredNames)}, " +
                $"but {string.Join(", ", uncoveredNames)}" +
                (uncoveredIds.Count > 4 ? $" and {uncoveredIds.Count - 4} more" : "") +
                " don't have bandwidth limits.";
        }

        // No coverage at all - don't list specific apps
        return $"{cellularContext}, but {categoryLabel} don't have bandwidth limits.";
    }

    #endregion
}

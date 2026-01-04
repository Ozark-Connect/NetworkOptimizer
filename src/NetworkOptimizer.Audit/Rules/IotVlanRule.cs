using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects IoT devices connected to non-IoT VLANs
/// Uses enhanced detection: fingerprint > MAC OUI > port name patterns
/// </summary>
public class IotVlanRule : AuditRuleBase
{
    public override string RuleId => "IOT-VLAN-001";
    public override string RuleName => "IoT Device VLAN Placement";
    public override string Description => "IoT devices should be on dedicated IoT VLANs for security isolation";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 10;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Skip uplinks, WAN ports, and non-access ports
        if (port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        DeviceDetectionResult detection;
        bool isOfflineDevice = false;

        if (port.IsUp && port.ConnectedClient != null)
        {
            // Active port with connected client: use full detection
            detection = DetectDeviceType(port);
        }
        else if (port.IsUp && port.ConnectedClient == null && HasOfflineDeviceData(port))
        {
            // Port is UP (link active) but no client connected (e.g., TV in standby)
            // Use LastConnectionMac or MAC restrictions for detection
            var offlineDetection = DetectDeviceTypeForDownPort(port);
            if (offlineDetection == null)
                return null;
            detection = offlineDetection;
            isOfflineDevice = true;
        }
        else if (!port.IsUp && IsAuditableDownPort(port))
        {
            // Down port: detect from last connection MAC or MAC restrictions
            var downPortDetection = DetectDeviceTypeForDownPort(port);
            if (downPortDetection == null)
                return null;
            detection = downPortDetection;
            isOfflineDevice = true;
        }
        else
        {
            // No connected client and no MAC data: skip
            return null;
        }

        // Check if this is an IoT or Printer device category
        var isPrinter = detection.Category == ClientDeviceCategory.Printer;
        if (!detection.Category.IsIoT() && !isPrinter)
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check placement using shared logic (with device allowance settings)
        var placement = isPrinter
            ? VlanPlacementChecker.CheckPrinterPlacement(network, networks, ScoreImpact, AllowanceSettings)
            : VlanPlacementChecker.CheckIoTPlacement(
                detection.Category, network, networks, ScoreImpact,
                AllowanceSettings, detection.VendorName);

        if (placement.IsCorrectlyPlaced)
            return null;

        // Determine severity and score based on recency for offline devices
        // Online devices: full score impact
        // Offline devices seen within 2 weeks: full score impact
        // Offline devices older than 2 weeks: Informational only (no score impact)
        var severity = placement.Severity;
        var scoreImpact = placement.ScoreImpact;

        if (isOfflineDevice)
        {
            var twoWeeksAgo = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
            var isRecentlyActive = port.LastConnectionSeen.HasValue && port.LastConnectionSeen.Value >= twoWeeksAgo;

            if (!isRecentlyActive)
            {
                severity = AuditSeverity.Informational;
                scoreImpact = 0;
            }
        }

        // Build device name based on port state
        string deviceName;
        if (isOfflineDevice)
        {
            // Offline device: prefer historical client name, then custom port name, then detected category
            var historicalName = port.HistoricalClient?.DisplayName
                ?? port.HistoricalClient?.Name
                ?? port.HistoricalClient?.Hostname;

            if (!string.IsNullOrEmpty(historicalName))
            {
                deviceName = $"{historicalName} on {port.Switch.Name}";
            }
            else
            {
                // Fall back to custom port name if set, otherwise detected category
                var hasCustomPortName = !string.IsNullOrEmpty(port.Name) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(port.Name, @"^Port \d+$");
                deviceName = hasCustomPortName
                    ? $"{port.Name} on {port.Switch.Name}"
                    : $"{detection.CategoryName} on {port.Switch.Name}";
            }
        }
        else
        {
            // Active port: use connected client name if available, fall back to detected category
            var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname;
            deviceName = !string.IsNullOrEmpty(clientName)
                ? $"{clientName} on {port.Switch.Name}"
                : $"{detection.CategoryName} on {port.Switch.Name}";
        }

        var message = $"{detection.CategoryName} on {network.Name} VLAN - should be isolated";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = severity,
            Message = message,
            DeviceName = deviceName,
            DeviceMac = port.Switch.MacAddress,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = placement.RecommendedNetwork?.Name,
            RecommendedVlan = placement.RecommendedNetwork?.VlanId,
            RecommendedAction = $"Move to {placement.RecommendedNetworkLabel}",
            Metadata = VlanPlacementChecker.BuildMetadata(detection, network),
            RuleId = RuleId,
            ScoreImpact = scoreImpact
        };
    }
}

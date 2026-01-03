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

        // Check if this is an IoT device category
        if (!detection.Category.IsIoT())
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check placement using shared logic (with device allowance settings)
        var placement = VlanPlacementChecker.CheckIoTPlacement(
            detection.Category, network, networks, ScoreImpact,
            AllowanceSettings, detection.VendorName);

        if (placement.IsCorrectlyPlaced)
            return null;

        // Build device name based on port state
        string deviceName;
        if (isOfflineDevice)
        {
            // Offline device: use port name or port number with switch context
            deviceName = !string.IsNullOrEmpty(port.Name)
                ? $"{port.Name} on {port.Switch.Name}"
                : $"Port {port.PortIndex} on {port.Switch.Name}";
        }
        else
        {
            // Active port: use connected client name if available
            var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname ?? port.Name;
            deviceName = clientName != null && clientName != port.Name
                ? $"{clientName} on {port.Switch.Name}"
                : $"{port.Name ?? $"Port {port.PortIndex}"} on {port.Switch.Name}";
        }

        // Adjust message for offline devices
        var statusNote = isOfflineDevice ? " (offline, MAC restricted)" : "";
        var message = $"{detection.CategoryName} on {network.Name} VLAN{statusNote} - should be isolated";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = placement.Severity,
            Message = message,
            DeviceName = deviceName,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = placement.RecommendedNetwork?.Name,
            RecommendedVlan = placement.RecommendedNetwork?.VlanId,
            RecommendedAction = $"Move to {placement.RecommendedNetworkLabel}",
            Metadata = VlanPlacementChecker.BuildMetadata(detection, network),
            RuleId = RuleId,
            ScoreImpact = placement.ScoreImpact
        };
    }
}

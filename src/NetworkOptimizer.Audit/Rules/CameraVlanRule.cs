using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects security cameras not on a dedicated security VLAN
/// Uses enhanced detection: fingerprint > MAC OUI > port name patterns
/// </summary>
public class CameraVlanRule : AuditRuleBase
{
    public override string RuleId => "CAMERA-VLAN-001";
    public override string RuleName => "Camera VLAN Placement";
    public override string Description => "Security cameras should be on dedicated security/camera VLANs";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 8;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Skip uplinks, WAN ports, and non-access ports
        if (port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        DeviceDetectionResult detection;
        bool isDownPort = false;

        if (port.IsUp)
        {
            // Active port: use full detection with connected client
            detection = DetectDeviceType(port);
        }
        else if (IsAuditableDownPort(port))
        {
            // Down port: detect from last connection MAC or MAC restrictions
            var downPortDetection = DetectDeviceTypeForDownPort(port);
            if (downPortDetection == null)
                return null;
            detection = downPortDetection;
            isDownPort = true;
        }
        else
        {
            // Down port without MAC restrictions: skip
            return null;
        }

        // Check if this is a surveillance/security device
        if (!detection.Category.IsSurveillance())
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check placement using shared logic
        var placement = VlanPlacementChecker.CheckCameraPlacement(network, networks, ScoreImpact);

        if (placement.IsCorrectlyPlaced)
            return null;

        // Build device name based on port state
        string deviceName;
        if (isDownPort)
        {
            // Down port: use port name or port number with switch context
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

        // Adjust message for down ports
        var statusNote = isDownPort ? " (port down, MAC restricted)" : "";
        var message = $"{detection.CategoryName} on {network.Name} VLAN{statusNote} - should be on security VLAN";

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

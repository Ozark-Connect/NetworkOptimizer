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
            // Offline device: use port name or port number with switch context
            deviceName = !string.IsNullOrEmpty(port.Name)
                ? $"{port.Name} on {port.Switch.Name}"
                : $"Port {port.PortIndex} on {port.Switch.Name}";
        }
        else
        {
            // Active port: use connected client name if available, fall back to detected category
            var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname;
            deviceName = !string.IsNullOrEmpty(clientName)
                ? $"{clientName} on {port.Switch.Name}"
                : $"{detection.CategoryName} on {port.Switch.Name}";
        }

        var message = $"{detection.CategoryName} on {network.Name} VLAN - should be on security VLAN";

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

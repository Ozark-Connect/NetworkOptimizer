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
        // Only check active access ports
        if (!port.IsUp || port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        // Use enhanced detection
        var detection = DetectDeviceType(port);

        // Check if this is an IoT device category
        if (!detection.Category.IsIoT())
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check if it's on an isolated network (IoT or Security are both acceptable)
        if (network.Purpose == NetworkPurpose.IoT || network.Purpose == NetworkPurpose.Security)
            return null; // Correctly placed on isolated network

        // Find the IoT network to recommend (prefer lower VLAN number)
        var iotNetwork = networks
            .Where(n => n.Purpose == NetworkPurpose.IoT)
            .OrderBy(n => n.VlanId)
            .FirstOrDefault();
        var recommendedVlan = iotNetwork != null
            ? $"{iotNetwork.Name} ({iotNetwork.VlanId})"
            : "IoT VLAN";

        // Low-risk IoT devices get Warning - users often keep them on main VLAN
        // Critical only for: SmartThermostat, SmartLock, SmartHub (security/control devices)
        var isLowRiskDevice = detection.Category is
            ClientDeviceCategory.SmartTV or
            ClientDeviceCategory.StreamingDevice or
            ClientDeviceCategory.MediaPlayer or
            ClientDeviceCategory.GameConsole or
            ClientDeviceCategory.SmartLighting or
            ClientDeviceCategory.SmartPlug or
            ClientDeviceCategory.SmartSpeaker or
            ClientDeviceCategory.RoboticVacuum;

        var severity = isLowRiskDevice ? AuditSeverity.Recommended : Severity;
        var scoreImpact = isLowRiskDevice ? 3 : ScoreImpact;

        // Use connected client name if available, otherwise port name - include switch context
        var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname ?? port.Name;
        var deviceName = clientName != null && clientName != port.Name
            ? $"{clientName} on {port.Switch.Name}"
            : $"{port.Name ?? $"Port {port.PortIndex}"} on {port.Switch.Name}";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = severity,
            Message = $"{detection.CategoryName} on {network.Name} VLAN - should be isolated",
            DeviceName = deviceName,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = iotNetwork?.Name,
            RecommendedVlan = iotNetwork?.VlanId,
            RecommendedAction = $"Move to {recommendedVlan}",
            Metadata = new Dictionary<string, object>
            {
                { "device_type", detection.CategoryName },
                { "device_category", detection.Category.ToString() },
                { "detection_source", detection.Source.ToString() },
                { "detection_confidence", detection.ConfidenceScore },
                { "vendor", detection.VendorName ?? "Unknown" },
                { "current_network_purpose", network.Purpose.ToString() }
            },
            RuleId = RuleId,
            ScoreImpact = scoreImpact
        };
    }
}

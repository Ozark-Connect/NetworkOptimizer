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

        // Check placement using shared logic
        var placement = VlanPlacementChecker.CheckIoTPlacement(
            detection.Category, network, networks, ScoreImpact);

        if (placement.IsCorrectlyPlaced)
            return null;

        // Use connected client name if available, otherwise port name - include switch context
        var clientName = port.ConnectedClient?.Name ?? port.ConnectedClient?.Hostname ?? port.Name;
        var deviceName = clientName != null && clientName != port.Name
            ? $"{clientName} on {port.Switch.Name}"
            : $"{port.Name ?? $"Port {port.PortIndex}"} on {port.Switch.Name}";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = placement.Severity,
            Message = $"{detection.CategoryName} on {network.Name} VLAN - should be isolated",
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

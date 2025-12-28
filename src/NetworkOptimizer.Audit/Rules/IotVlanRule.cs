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

        // Find the IoT network to recommend
        var iotNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.IoT);
        var recommendedVlan = iotNetwork != null
            ? $"{iotNetwork.Name} ({iotNetwork.VlanId})"
            : "IoT VLAN";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = Severity,
            Message = $"{detection.CategoryName} on {network.Name} VLAN - should be isolated",
            DeviceName = port.Switch.Name,
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
            ScoreImpact = ScoreImpact
        };
    }
}

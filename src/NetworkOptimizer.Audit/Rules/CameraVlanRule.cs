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
        // Only check active access ports
        if (!port.IsUp || port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        // Use enhanced detection
        var detection = DetectDeviceType(port);

        // Check if this is a surveillance/security device
        if (!detection.Category.IsSurveillance())
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check if it's on a security network
        if (network.Purpose == NetworkPurpose.Security)
            return null; // Correctly placed

        // Find the security network to recommend
        var securityNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Security);
        var recommendedVlan = securityNetwork != null
            ? $"{securityNetwork.Name} ({securityNetwork.VlanId})"
            : "Security VLAN";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = Severity,
            Message = $"{detection.CategoryName} on {network.Name} VLAN - should be on security VLAN",
            DeviceName = port.Switch.Name,
            Port = port.PortIndex.ToString(),
            PortName = port.Name,
            CurrentNetwork = network.Name,
            CurrentVlan = network.VlanId,
            RecommendedNetwork = securityNetwork?.Name,
            RecommendedVlan = securityNetwork?.VlanId,
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

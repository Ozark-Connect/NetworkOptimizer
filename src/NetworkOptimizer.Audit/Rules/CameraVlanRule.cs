using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects security cameras not on a dedicated security VLAN
/// Cameras should be isolated on security VLANs
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

        // Check if port name suggests a camera
        if (!IsCameraDeviceName(port.Name))
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check if it's on a security network
        // DEBUG: Log network purpose for troubleshooting
        Console.WriteLine($"[CameraVlanRule] Camera '{port.Name}' on network '{network.Name}' (ID: {network.Id}) has Purpose: {network.Purpose}");

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
            Message = $"Camera on {network.Name} VLAN - should be on security VLAN",
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
                { "device_type", "Camera" },
                { "current_network_purpose", network.Purpose.ToString() }
            },
            RuleId = RuleId,
            ScoreImpact = ScoreImpact
        };
    }
}

using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects IoT devices connected to non-IoT VLANs
/// Checks port names for IoT device patterns (IKEA, Hue, Smart, etc.)
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

        // Check if port name suggests an IoT device
        if (!IsIoTDeviceName(port.Name))
            return null;

        // Get the network this port is on
        var network = GetNetwork(port.NativeNetworkId, networks);
        if (network == null)
            return null;

        // Check if it's on an IoT network
        if (network.Purpose == NetworkPurpose.IoT)
            return null; // Correctly placed

        // Find the IoT network to recommend
        var iotNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.IoT);
        var recommendedVlan = iotNetwork != null
            ? $"{iotNetwork.Name} ({iotNetwork.VlanId})"
            : "IoT VLAN";

        return new AuditIssue
        {
            Type = RuleId,
            Severity = Severity,
            Message = $"IoT device on {network.Name} VLAN - should be isolated",
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
                { "device_type", "IoT" },
                { "current_network_purpose", network.Purpose.ToString() }
            },
            RuleId = RuleId,
            ScoreImpact = ScoreImpact
        };
    }
}

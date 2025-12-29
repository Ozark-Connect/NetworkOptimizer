using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects wireless IoT devices connected to non-IoT VLANs
/// </summary>
public class WirelessIotVlanRule : WirelessAuditRuleBase
{
    public override string RuleId => "WIFI-IOT-VLAN-001";
    public override string RuleName => "Wireless IoT Device VLAN Placement";
    public override string Description => "Wireless IoT devices should be on dedicated IoT networks for security isolation";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 10;

    public override AuditIssue? Evaluate(WirelessClientInfo client, List<NetworkInfo> networks)
    {
        // Check if this is an IoT device category
        if (!client.Detection.Category.IsIoT())
            return null;

        // Get the network this client is on
        var network = client.Network;
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
        var recommendedVlanStr = iotNetwork != null
            ? $"{iotNetwork.Name} ({iotNetwork.VlanId})"
            : "IoT WiFi network";

        // Low-risk IoT devices get Warning - users often keep them on main WiFi
        // Critical only for: SmartThermostat, SmartLock, SmartHub (security/control devices)
        var isLowRiskDevice = client.Detection.Category is
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

        return CreateIssue(
            $"{client.Detection.CategoryName} on {network.Name} WiFi - should be isolated",
            client,
            severityOverride: severity,
            scoreImpactOverride: scoreImpact,
            recommendedNetwork: iotNetwork?.Name,
            recommendedVlan: iotNetwork?.VlanId,
            recommendedAction: $"Connect to {recommendedVlanStr}",
            metadata: new Dictionary<string, object>
            {
                { "device_type", client.Detection.CategoryName },
                { "device_category", client.Detection.Category.ToString() },
                { "detection_source", client.Detection.Source.ToString() },
                { "detection_confidence", client.Detection.ConfidenceScore },
                { "vendor", client.Detection.VendorName ?? "Unknown" },
                { "current_network_purpose", network.Purpose.ToString() },
                { "is_low_risk_device", isLowRiskDevice }
            }
        );
    }
}

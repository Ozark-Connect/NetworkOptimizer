using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects wireless security cameras not on a dedicated security VLAN
/// </summary>
public class WirelessCameraVlanRule : WirelessAuditRuleBase
{
    public override string RuleId => "WIFI-CAMERA-VLAN-001";
    public override string RuleName => "Wireless Camera VLAN Placement";
    public override string Description => "Wireless security cameras should be on dedicated security networks";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 8;

    public override AuditIssue? Evaluate(WirelessClientInfo client, List<NetworkInfo> networks)
    {
        // Check if this is a surveillance/security device
        if (!client.Detection.Category.IsSurveillance())
            return null;

        // Get the network this client is on
        var network = client.Network;
        if (network == null)
            return null;

        // Check if it's on a security network
        if (network.Purpose == NetworkPurpose.Security)
            return null; // Correctly placed

        // Find the security network to recommend (prefer lower VLAN number)
        var securityNetwork = networks
            .Where(n => n.Purpose == NetworkPurpose.Security)
            .OrderBy(n => n.VlanId)
            .FirstOrDefault();
        var recommendedVlanStr = securityNetwork != null
            ? $"{securityNetwork.Name} ({securityNetwork.VlanId})"
            : "Security WiFi network";

        return CreateIssue(
            $"{client.Detection.CategoryName} on {network.Name} WiFi - should be on security network",
            client,
            recommendedNetwork: securityNetwork?.Name,
            recommendedVlan: securityNetwork?.VlanId,
            recommendedAction: $"Connect to {recommendedVlanStr}",
            metadata: new Dictionary<string, object>
            {
                { "device_type", client.Detection.CategoryName },
                { "device_category", client.Detection.Category.ToString() },
                { "detection_source", client.Detection.Source.ToString() },
                { "detection_confidence", client.Detection.ConfidenceScore },
                { "vendor", client.Detection.VendorName ?? "Unknown" },
                { "current_network_purpose", network.Purpose.ToString() }
            }
        );
    }
}

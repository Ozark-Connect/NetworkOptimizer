using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects wireless self-hosted security cameras not on a dedicated security VLAN.
/// Note: Cloud surveillance (Ring, Nest, Wyze, Blink, Arlo, SimpliSafe) are handled by IoT VLAN rules instead.
/// </summary>
public class WirelessCameraVlanRule : WirelessAuditRuleBase
{
    public override string RuleId => "WIFI-CAMERA-VLAN-001";
    public override string RuleName => "Wireless Camera VLAN Placement";
    public override string Description => "Wireless self-hosted security cameras should be on dedicated security networks";
    public override AuditSeverity Severity => AuditSeverity.Critical;
    public override int ScoreImpact => 8;

    public override AuditIssue? Evaluate(WirelessClientInfo client, List<NetworkInfo> networks)
    {
        // Check if this is a surveillance/security device (but not cloud-based ones)
        // Cloud surveillance (Ring, Nest, Wyze, Blink, Arlo, SimpliSafe) are handled by IoT VLAN rules
        if (!client.Detection.Category.IsSurveillance())
            return null;

        // Skip cloud surveillance devices - they need internet so should go on IoT VLAN, not Security VLAN
        if (client.Detection.Category.IsCloudSurveillance())
            return null;

        // Get the network this client is on
        var network = client.Network;
        if (network == null)
            return null;

        // Check placement using shared logic
        var placement = VlanPlacementChecker.CheckCameraPlacement(network, networks, ScoreImpact);

        if (placement.IsCorrectlyPlaced)
            return null;

        return CreateIssue(
            $"{client.Detection.CategoryName} on {network.Name} VLAN - should be on security VLAN",
            client,
            recommendedNetwork: placement.RecommendedNetwork?.Name,
            recommendedVlan: placement.RecommendedNetwork?.VlanId,
            recommendedAction: VlanPlacementChecker.GetMoveRecommendation(placement.RecommendedNetworkLabel),
            metadata: VlanPlacementChecker.BuildMetadata(client.Detection, network)
        );
    }
}

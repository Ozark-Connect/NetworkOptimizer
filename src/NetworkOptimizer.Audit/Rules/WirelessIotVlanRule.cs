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
        // Check if this is an IoT or Printer device category
        var isPrinter = client.Detection.Category == ClientDeviceCategory.Printer ||
            client.Detection.Category == ClientDeviceCategory.Scanner;
        if (!client.Detection.Category.IsIoT() && !isPrinter)
            return null;

        // Get the network this client is on
        var network = client.Network;
        if (network == null)
            return null;

        // Check placement using shared logic (with device allowance settings)
        var placement = isPrinter
            ? VlanPlacementChecker.CheckPrinterPlacement(network, networks, ScoreImpact, AllowanceSettings)
            : VlanPlacementChecker.CheckIoTPlacement(
                client.Detection.Category, network, networks, ScoreImpact,
                AllowanceSettings, client.Detection.VendorName);

        if (placement.IsCorrectlyPlaced)
            return null;

        // Different messaging for allowed vs not-allowed devices
        string message;
        string recommendedAction;
        if (placement.IsAllowedBySettings)
        {
            message = $"{client.Detection.CategoryName} allowed per Settings on {network.Name} VLAN";
            recommendedAction = "Change in Settings if you want to isolate this device type";
        }
        else
        {
            message = $"{client.Detection.CategoryName} on {network.Name} VLAN - should be isolated";
            recommendedAction = $"Move to {placement.RecommendedNetworkLabel}";
        }

        var metadata = VlanPlacementChecker.BuildMetadata(client.Detection, network, placement.IsLowRisk);
        if (placement.IsAllowedBySettings)
        {
            metadata["allowed_by_settings"] = true;
        }

        return CreateIssue(
            message,
            client,
            severityOverride: placement.Severity,
            scoreImpactOverride: placement.ScoreImpact,
            recommendedNetwork: placement.RecommendedNetwork?.Name,
            recommendedVlan: placement.RecommendedNetwork?.VlanId,
            recommendedAction: recommendedAction,
            metadata: metadata
        );
    }
}

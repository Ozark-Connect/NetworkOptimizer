using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects unused ports that are not disabled
/// Unused ports should be disabled to prevent unauthorized connections
/// </summary>
public class UnusedPortRule : AuditRuleBase
{
    public override string RuleId => "UNUSED-PORT-001";
    public override string RuleName => "Unused Port Disabled";
    public override string Description => "Unused ports should be disabled (forward: disabled) to prevent unauthorized access";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 2;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Only check ports that are down
        if (port.IsUp)
            return null;

        // Skip uplinks and WAN ports
        if (port.IsUplink || port.IsWan)
            return null;

        // Check if port is disabled
        if (port.ForwardMode == "disabled")
            return null; // Correctly configured

        return CreateIssue(
            "Unused port not disabled - should set forward mode to 'disabled'",
            port,
            new Dictionary<string, object>
            {
                { "current_forward_mode", port.ForwardMode ?? "unknown" },
                { "recommendation", "Set forward mode to 'disabled' to harden the switch" }
            });
    }
}

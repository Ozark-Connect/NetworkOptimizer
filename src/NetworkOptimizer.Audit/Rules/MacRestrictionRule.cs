using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects access ports without MAC address restrictions
/// MAC restrictions help prevent unauthorized device connections
/// </summary>
public class MacRestrictionRule : AuditRuleBase
{
    public override string RuleId => "MAC-RESTRICT-001";
    public override string RuleName => "MAC Address Restriction";
    public override string Description => "Access ports should have MAC restrictions to prevent unauthorized devices";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 3;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Only check active access ports
        if (!port.IsUp || port.ForwardMode != "native" || port.IsUplink || port.IsWan)
            return null;

        // Check if switch supports MAC ACLs
        if (port.Switch.Capabilities.MaxCustomMacAcls == 0)
            return null; // Switch doesn't support this feature

        // Check if port has MAC restrictions
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false))
            return null; // Already has restrictions

        var network = GetNetwork(port.NativeNetworkId, networks);

        return CreateIssue(
            "No MAC restriction on access port",
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" },
                { "recommendation", "Configure port security with allowed MAC addresses" }
            });
    }
}

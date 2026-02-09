using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects firewall rules that allow any source to any destination
/// These rules are overly permissive and reduce security
/// Note: This rule operates on firewall rules, not ports, so it's used differently
/// </summary>
public class FirewallAnyAnyRule
{
    /// <summary>
    /// Check if a firewall rule is overly permissive (any->any)
    /// Handles both v2 API format (SourceMatchingTarget) and legacy format (SourceType/Source)
    /// </summary>
    public static bool IsAnyAnyRule(FirewallRule rule)
    {
        if (!rule.Enabled)
            return false;

        // v2 API uses SourceMatchingTarget/DestinationMatchingTarget = "ANY"
        // Legacy API uses SourceType/DestinationType = "any" or empty Source/Destination
        var isAnySource = rule.IsAnySource();
        var isAnyDest = rule.IsAnyDestination();
        var isAnyProtocol = rule.Protocol?.Equals("all", StringComparison.OrdinalIgnoreCase) == true
            || string.IsNullOrEmpty(rule.Protocol);

        var hasSpecificPorts = !string.IsNullOrEmpty(rule.DestinationPort)
            || !string.IsNullOrEmpty(rule.SourcePort);

        return isAnySource && isAnyDest && isAnyProtocol && !hasSpecificPorts && rule.ActionType.IsAllowAction();
    }

    /// <summary>
    /// Create audit issue for any->any firewall rule
    /// </summary>
    public static AuditIssue CreateIssue(FirewallRule rule)
    {
        return new AuditIssue
        {
            Type = IssueTypes.FwAnyAny,
            Severity = AuditSeverity.Critical,
            Message = $"Firewall rule '{rule.Name}' allows any->any traffic",
            Metadata = new Dictionary<string, object>
            {
                { "rule_id", rule.Id },
                { "rule_name", rule.Name ?? "Unnamed" },
                { "rule_index", rule.Index },
                { "ruleset", rule.Ruleset ?? "unknown" },
                { "action", rule.Action ?? "unknown" }
            },
            RecommendedAction = "Restrict source, destination, or protocol to minimum required access",
            RuleId = "FW-ANY-ANY-001",
            ScoreImpact = 15
        };
    }
}

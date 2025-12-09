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
    /// </summary>
    public static bool IsAnyAnyRule(FirewallRule rule)
    {
        if (!rule.Enabled)
            return false;

        var isAnySource = rule.SourceType == "any" || string.IsNullOrEmpty(rule.Source);
        var isAnyDest = rule.DestinationType == "any" || string.IsNullOrEmpty(rule.Destination);
        var isAnyProtocol = rule.Protocol == "all" || string.IsNullOrEmpty(rule.Protocol);

        return isAnySource && isAnyDest && isAnyProtocol && rule.Action == "accept";
    }

    /// <summary>
    /// Create audit issue for any->any firewall rule
    /// </summary>
    public static AuditIssue CreateIssue(FirewallRule rule)
    {
        return new AuditIssue
        {
            Type = "FW_ANY_ANY",
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

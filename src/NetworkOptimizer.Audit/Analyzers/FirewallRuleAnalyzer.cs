using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Analyzes firewall rules for security issues
/// </summary>
public class FirewallRuleAnalyzer
{
    private readonly ILogger<FirewallRuleAnalyzer> _logger;

    public FirewallRuleAnalyzer(ILogger<FirewallRuleAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extract firewall rules from UniFi device JSON
    /// </summary>
    public List<FirewallRule> ExtractFirewallRules(JsonElement deviceData)
    {
        var rules = new List<FirewallRule>();

        // Handle both single device and array of devices
        var devices = deviceData.ValueKind == JsonValueKind.Array
            ? deviceData.EnumerateArray().ToList()
            : new List<JsonElement> { deviceData };

        // Handle wrapped response with "data" property
        if (deviceData.ValueKind == JsonValueKind.Object && deviceData.TryGetProperty("data", out var dataArray))
        {
            devices = dataArray.EnumerateArray().ToList();
        }

        foreach (var device in devices)
        {
            // Look for gateway device with firewall rules
            if (!device.TryGetProperty("type", out var typeElement))
                continue;

            var deviceType = typeElement.GetString();
            if (deviceType is not ("ugw" or "udm" or "uxg"))
                continue;

            // Check for firewall_rules or firewall_groups
            if (device.TryGetProperty("firewall_rules", out var fwRules) && fwRules.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in fwRules.EnumerateArray())
                {
                    var parsed = ParseFirewallRule(rule);
                    if (parsed != null)
                        rules.Add(parsed);
                }
            }
        }

        _logger.LogInformation("Extracted {RuleCount} firewall rules", rules.Count);
        return rules;
    }

    /// <summary>
    /// Parse a single firewall rule from JSON
    /// </summary>
    private FirewallRule? ParseFirewallRule(JsonElement rule)
    {
        // Get rule ID
        var id = rule.TryGetProperty("_id", out var idProp)
            ? idProp.GetString()
            : rule.TryGetProperty("rule_id", out var ruleIdProp)
                ? ruleIdProp.GetString()
                : null;

        if (string.IsNullOrEmpty(id))
            return null;

        // Get basic properties
        var name = rule.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()
            : null;

        var enabled = !rule.TryGetProperty("enabled", out var enabledProp) || enabledProp.GetBoolean();

        var index = rule.TryGetProperty("rule_index", out var indexProp)
            ? indexProp.GetInt32()
            : 0;

        var action = rule.TryGetProperty("action", out var actionProp)
            ? actionProp.GetString()
            : null;

        var protocol = rule.TryGetProperty("protocol", out var protocolProp)
            ? protocolProp.GetString()
            : null;

        // Source information
        var sourceType = rule.TryGetProperty("src_type", out var srcTypeProp)
            ? srcTypeProp.GetString()
            : null;

        var source = rule.TryGetProperty("src_address", out var srcAddrProp)
            ? srcAddrProp.GetString()
            : rule.TryGetProperty("src_network_id", out var srcNetProp)
                ? srcNetProp.GetString()
                : null;

        var sourcePort = rule.TryGetProperty("src_port", out var srcPortProp)
            ? srcPortProp.GetString()
            : null;

        // Destination information
        var destType = rule.TryGetProperty("dst_type", out var dstTypeProp)
            ? dstTypeProp.GetString()
            : null;

        var destination = rule.TryGetProperty("dst_address", out var dstAddrProp)
            ? dstAddrProp.GetString()
            : rule.TryGetProperty("dst_network_id", out var dstNetProp)
                ? dstNetProp.GetString()
                : null;

        var destinationPort = rule.TryGetProperty("dst_port", out var dstPortProp)
            ? dstPortProp.GetString()
            : null;

        // Statistics
        var hitCount = rule.TryGetProperty("hit_count", out var hitCountProp) && hitCountProp.ValueKind == JsonValueKind.Number
            ? hitCountProp.GetInt64()
            : 0;

        var ruleset = rule.TryGetProperty("ruleset", out var rulesetProp)
            ? rulesetProp.GetString()
            : null;

        return new FirewallRule
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            Index = index,
            Action = action,
            Protocol = protocol,
            SourceType = sourceType,
            Source = source,
            SourcePort = sourcePort,
            DestinationType = destType,
            Destination = destination,
            DestinationPort = destinationPort,
            HasBeenHit = hitCount > 0,
            HitCount = hitCount,
            Ruleset = ruleset
        };
    }

    /// <summary>
    /// Detect shadowed rules (rules that will never be hit due to earlier rules)
    /// </summary>
    public List<AuditIssue> DetectShadowedRules(List<FirewallRule> rules)
    {
        var issues = new List<AuditIssue>();

        // Group by ruleset
        var rulesets = rules.GroupBy(r => r.Ruleset ?? "default");

        foreach (var ruleset in rulesets)
        {
            var orderedRules = ruleset.OrderBy(r => r.Index).ToList();

            for (int i = 0; i < orderedRules.Count; i++)
            {
                var currentRule = orderedRules[i];

                // Skip disabled rules
                if (!currentRule.Enabled)
                    continue;

                // Check if any earlier rule shadows this one
                for (int j = 0; j < i; j++)
                {
                    var earlierRule = orderedRules[j];

                    if (!earlierRule.Enabled)
                        continue;

                    // Check if earlier rule is more permissive and shadows current
                    if (IsShadowedBy(currentRule, earlierRule))
                    {
                        issues.Add(new AuditIssue
                        {
                            Type = "SHADOWED_RULE",
                            Severity = AuditSeverity.Investigate,
                            Message = $"Rule '{currentRule.Name}' (index {currentRule.Index}) is shadowed by earlier rule '{earlierRule.Name}' (index {earlierRule.Index})",
                            Metadata = new Dictionary<string, object>
                            {
                                { "shadowed_rule", currentRule.Name ?? currentRule.Id },
                                { "shadowed_index", currentRule.Index },
                                { "shadowing_rule", earlierRule.Name ?? earlierRule.Id },
                                { "shadowing_index", earlierRule.Index },
                                { "ruleset", currentRule.Ruleset ?? "default" }
                            },
                            RuleId = "FW-SHADOW-001",
                            ScoreImpact = 2
                        });
                        break; // Only report the first shadowing rule
                    }
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Check if a rule is shadowed by another rule
    /// </summary>
    private bool IsShadowedBy(FirewallRule current, FirewallRule earlier)
    {
        // Simple heuristic: if earlier rule is "any/any" or matches same criteria
        // This is a simplified version - full implementation would need more sophisticated matching

        // Check if earlier rule has same action
        if (earlier.Action != current.Action)
            return false;

        // Check if earlier rule is more permissive
        var earlierIsAnySource = earlier.SourceType == "any" || string.IsNullOrEmpty(earlier.Source);
        var earlierIsAnyDest = earlier.DestinationType == "any" || string.IsNullOrEmpty(earlier.Destination);

        var currentIsAnySource = current.SourceType == "any" || string.IsNullOrEmpty(current.Source);
        var currentIsAnyDest = current.DestinationType == "any" || string.IsNullOrEmpty(current.Destination);

        // If earlier rule is any->any and protocols match
        if (earlierIsAnySource && earlierIsAnyDest &&
            (earlier.Protocol == current.Protocol || earlier.Protocol == "all"))
        {
            return true;
        }

        // If sources and destinations match exactly
        if (earlier.Source == current.Source &&
            earlier.Destination == current.Destination &&
            earlier.Protocol == current.Protocol)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Detect overly permissive rules (any/any)
    /// </summary>
    public List<AuditIssue> DetectPermissiveRules(List<FirewallRule> rules)
    {
        var issues = new List<AuditIssue>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
                continue;

            // Check for any->any rules
            var isAnySource = rule.SourceType == "any" || string.IsNullOrEmpty(rule.Source);
            var isAnyDest = rule.DestinationType == "any" || string.IsNullOrEmpty(rule.Destination);
            var isAnyProtocol = rule.Protocol == "all" || string.IsNullOrEmpty(rule.Protocol);

            if (isAnySource && isAnyDest && isAnyProtocol && rule.Action == "accept")
            {
                issues.Add(new AuditIssue
                {
                    Type = "PERMISSIVE_RULE",
                    Severity = AuditSeverity.Critical,
                    Message = $"Overly permissive rule '{rule.Name}' allows any->any traffic",
                    Metadata = new Dictionary<string, object>
                    {
                        { "rule_name", rule.Name ?? rule.Id },
                        { "rule_index", rule.Index },
                        { "ruleset", rule.Ruleset ?? "default" },
                        { "recommendation", "Restrict source, destination, or protocol" }
                    },
                    RuleId = "FW-PERMISSIVE-001",
                    ScoreImpact = 15
                });
            }
            // Check for any source or any destination (less critical)
            else if ((isAnySource || isAnyDest) && rule.Action == "accept")
            {
                var direction = isAnySource ? "any source" : "any destination";
                issues.Add(new AuditIssue
                {
                    Type = "BROAD_RULE",
                    Severity = AuditSeverity.Recommended,
                    Message = $"Broad rule '{rule.Name}' allows traffic from/to {direction}",
                    Metadata = new Dictionary<string, object>
                    {
                        { "rule_name", rule.Name ?? rule.Id },
                        { "rule_index", rule.Index },
                        { "ruleset", rule.Ruleset ?? "default" },
                        { "direction", direction }
                    },
                    RuleId = "FW-BROAD-001",
                    ScoreImpact = 5
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Detect orphaned rules (referencing deleted groups or networks)
    /// </summary>
    public List<AuditIssue> DetectOrphanedRules(List<FirewallRule> rules, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();
        var networkIds = new HashSet<string>(networks.Select(n => n.Id));

        foreach (var rule in rules)
        {
            if (!rule.Enabled)
                continue;

            // Check if source references a network that doesn't exist
            if (rule.SourceType == "network" && !string.IsNullOrEmpty(rule.Source))
            {
                if (!networkIds.Contains(rule.Source))
                {
                    issues.Add(new AuditIssue
                    {
                        Type = "ORPHANED_RULE",
                        Severity = AuditSeverity.Investigate,
                        Message = $"Rule '{rule.Name}' references non-existent source network",
                        Metadata = new Dictionary<string, object>
                        {
                            { "rule_name", rule.Name ?? rule.Id },
                            { "rule_index", rule.Index },
                            { "missing_network_id", rule.Source }
                        },
                        RuleId = "FW-ORPHAN-001",
                        ScoreImpact = 3
                    });
                }
            }

            // Check if destination references a network that doesn't exist
            if (rule.DestinationType == "network" && !string.IsNullOrEmpty(rule.Destination))
            {
                if (!networkIds.Contains(rule.Destination))
                {
                    issues.Add(new AuditIssue
                    {
                        Type = "ORPHANED_RULE",
                        Severity = AuditSeverity.Investigate,
                        Message = $"Rule '{rule.Name}' references non-existent destination network",
                        Metadata = new Dictionary<string, object>
                        {
                            { "rule_name", rule.Name ?? rule.Id },
                            { "rule_index", rule.Index },
                            { "missing_network_id", rule.Destination }
                        },
                        RuleId = "FW-ORPHAN-002",
                        ScoreImpact = 3
                    });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Check for missing inter-VLAN isolation rules
    /// </summary>
    public List<AuditIssue> CheckInterVlanIsolation(List<FirewallRule> rules, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        // Find networks that should be isolated
        var iotNetworks = networks.Where(n => n.Purpose == NetworkPurpose.IoT).ToList();
        var guestNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Guest).ToList();
        var corporateNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Corporate).ToList();

        // Check if there are explicit deny rules between IoT and Corporate
        foreach (var iot in iotNetworks)
        {
            foreach (var corporate in corporateNetworks)
            {
                var hasIsolationRule = rules.Any(r =>
                    r.Enabled &&
                    r.Action == "drop" &&
                    ((r.Source == iot.Id && r.Destination == corporate.Id) ||
                     (r.Source == corporate.Id && r.Destination == iot.Id)));

                if (!hasIsolationRule)
                {
                    issues.Add(new AuditIssue
                    {
                        Type = "MISSING_ISOLATION",
                        Severity = AuditSeverity.Recommended,
                        Message = $"No explicit isolation rule between {iot.Name} and {corporate.Name}",
                        Metadata = new Dictionary<string, object>
                        {
                            { "network1", iot.Name },
                            { "network2", corporate.Name },
                            { "recommendation", "Add firewall rule to block inter-VLAN traffic" }
                        },
                        RuleId = "FW-ISOLATION-001",
                        ScoreImpact = 7
                    });
                }
            }
        }

        // Similar check for Guest networks
        foreach (var guest in guestNetworks)
        {
            foreach (var corporate in corporateNetworks)
            {
                var hasIsolationRule = rules.Any(r =>
                    r.Enabled &&
                    r.Action == "drop" &&
                    ((r.Source == guest.Id && r.Destination == corporate.Id) ||
                     (r.Source == corporate.Id && r.Destination == guest.Id)));

                if (!hasIsolationRule)
                {
                    issues.Add(new AuditIssue
                    {
                        Type = "MISSING_ISOLATION",
                        Severity = AuditSeverity.Recommended,
                        Message = $"No explicit isolation rule between {guest.Name} and {corporate.Name}",
                        Metadata = new Dictionary<string, object>
                        {
                            { "network1", guest.Name },
                            { "network2", corporate.Name },
                            { "recommendation", "Add firewall rule to block inter-VLAN traffic" }
                        },
                        RuleId = "FW-ISOLATION-002",
                        ScoreImpact = 7
                    });
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Run all firewall analyses
    /// </summary>
    public List<AuditIssue> AnalyzeFirewallRules(List<FirewallRule> rules, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        _logger.LogInformation("Analyzing {RuleCount} firewall rules", rules.Count);

        issues.AddRange(DetectShadowedRules(rules));
        issues.AddRange(DetectPermissiveRules(rules));
        issues.AddRange(DetectOrphanedRules(rules, networks));
        issues.AddRange(CheckInterVlanIsolation(rules, networks));

        _logger.LogInformation("Found {IssueCount} firewall issues", issues.Count);

        return issues;
    }
}

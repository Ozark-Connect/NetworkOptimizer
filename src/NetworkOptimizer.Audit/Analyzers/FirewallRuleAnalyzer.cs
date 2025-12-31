using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

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

        _logger.LogInformation("Extracted {RuleCount} firewall rules from device data", rules.Count);
        return rules;
    }

    /// <summary>
    /// Extract firewall rules from UniFi firewall policies API response
    /// </summary>
    public List<FirewallRule> ExtractFirewallPolicies(JsonElement? firewallPoliciesData)
    {
        var rules = new List<FirewallRule>();

        if (!firewallPoliciesData.HasValue)
        {
            _logger.LogDebug("No firewall policies data provided");
            return rules;
        }

        // Parse policies array (uses UnwrapDataArray to handle both direct array and {data: [...]} wrapper)
        foreach (var policy in firewallPoliciesData.Value.UnwrapDataArray())
        {
            var parsed = ParseFirewallPolicy(policy);
            if (parsed != null)
                rules.Add(parsed);
        }

        _logger.LogInformation("Extracted {RuleCount} firewall rules from policies API", rules.Count);
        return rules;
    }

    /// <summary>
    /// Parse a single firewall policy from the v2 API format
    /// </summary>
    private FirewallRule? ParseFirewallPolicy(JsonElement policy)
    {
        var id = policy.GetStringOrNull("_id");
        if (string.IsNullOrEmpty(id))
            return null;

        var name = policy.GetStringOrNull("name");
        var enabled = policy.GetBoolOrDefault("enabled", true);
        var action = policy.GetStringOrNull("action");
        var protocol = policy.GetStringOrNull("protocol");
        var index = policy.GetIntOrDefault("index", 0);

        // Extract source network IDs
        List<string>? sourceNetworkIds = null;
        if (policy.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            if (source.TryGetProperty("network_ids", out var netIds) && netIds.ValueKind == JsonValueKind.Array)
            {
                sourceNetworkIds = netIds.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }

        // Extract destination info including web domains
        string? destPort = null;
        string? destType = null;
        List<string>? webDomains = null;
        if (policy.TryGetProperty("destination", out var dest) && dest.ValueKind == JsonValueKind.Object)
        {
            destPort = dest.GetStringOrNull("port");
            destType = dest.GetStringOrNull("matching_target");

            if (dest.TryGetProperty("web_domains", out var domains) && domains.ValueKind == JsonValueKind.Array)
            {
                webDomains = domains.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }

        return new FirewallRule
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            Index = index,
            Action = action,
            Protocol = protocol,
            DestinationType = destType,
            DestinationPort = destPort,
            SourceNetworkIds = sourceNetworkIds,
            WebDomains = webDomains
        };
    }

    /// <summary>
    /// Parse a single firewall rule from JSON (legacy format)
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

        // Extract source network IDs (supports both nested and flat formats)
        List<string>? sourceNetworkIds = null;
        if (rule.TryGetProperty("source", out var sourceObj) && sourceObj.ValueKind == JsonValueKind.Object)
        {
            if (sourceObj.TryGetProperty("network_ids", out var netIds) && netIds.ValueKind == JsonValueKind.Array)
            {
                sourceNetworkIds = netIds.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }
        // Fallback to flat format
        if (sourceNetworkIds == null && !string.IsNullOrEmpty(source))
        {
            sourceNetworkIds = new List<string> { source };
        }

        // Extract web domains (from nested destination object)
        List<string>? webDomains = null;
        if (rule.TryGetProperty("destination", out var destObj) && destObj.ValueKind == JsonValueKind.Object)
        {
            if (destObj.TryGetProperty("web_domains", out var domains) && domains.ValueKind == JsonValueKind.Array)
            {
                webDomains = domains.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }

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
            Ruleset = ruleset,
            SourceNetworkIds = sourceNetworkIds,
            WebDomains = webDomains
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

    /// <summary>
    /// Analyze firewall rules for isolated management networks.
    /// When a management network has isolation enabled but internet disabled,
    /// it needs specific firewall rules to allow UniFi cloud, AFC, and device registration traffic.
    /// </summary>
    public List<AuditIssue> AnalyzeManagementNetworkFirewallAccess(List<FirewallRule> rules, List<NetworkInfo> networks, bool has5GDevice = false)
    {
        var issues = new List<AuditIssue>();

        // Find management networks that are isolated and don't have internet access
        var isolatedMgmtNetworks = networks.Where(n =>
            n.Purpose == NetworkPurpose.Management &&
            n.NetworkIsolationEnabled &&
            !n.InternetAccessEnabled).ToList();

        if (!isolatedMgmtNetworks.Any())
        {
            _logger.LogDebug("No isolated management networks without internet access found");
            return issues;
        }

        foreach (var mgmtNetwork in isolatedMgmtNetworks)
        {
            _logger.LogDebug("Checking firewall access for isolated management network '{Name}'", mgmtNetwork.Name);

            // Check for UniFi cloud access rule
            var hasUniFiAccess = rules.Any(r =>
                r.Enabled &&
                r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                (r.SourceNetworkIds?.Contains(mgmtNetwork.Id) == true ||
                 r.Name?.Contains("UniFi", StringComparison.OrdinalIgnoreCase) == true) &&
                (r.WebDomains?.Any(d => d.Contains("ui.com", StringComparison.OrdinalIgnoreCase)) == true ||
                 r.Name?.Contains("ui.com", StringComparison.OrdinalIgnoreCase) == true));

            if (!hasUniFiAccess)
            {
                issues.Add(new AuditIssue
                {
                    Type = "MGMT_MISSING_UNIFI_ACCESS",
                    Severity = AuditSeverity.Info,
                    Message = $"Isolated management network '{mgmtNetwork.Name}' may lack UniFi cloud access",
                    CurrentNetwork = mgmtNetwork.Name,
                    CurrentVlan = mgmtNetwork.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", mgmtNetwork.Name },
                        { "vlan", mgmtNetwork.VlanId },
                        { "required_domain", "ui.com" }
                    },
                    RuleId = "FW-MGMT-001",
                    ScoreImpact = 0,
                    RecommendedAction = "Add firewall rule allowing TCP 443 to ui.com for UniFi cloud management"
                });
            }

            // Check for AFC (Automated Frequency Coordination) traffic rule - needed for 6GHz WiFi
            var hasAfcAccess = rules.Any(r =>
                r.Enabled &&
                r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                (r.SourceNetworkIds?.Contains(mgmtNetwork.Id) == true ||
                 r.Name?.Contains("AFC", StringComparison.OrdinalIgnoreCase) == true) &&
                (r.WebDomains?.Any(d => d.Contains("qcs.qualcomm.com", StringComparison.OrdinalIgnoreCase)) == true ||
                 r.Name?.Contains("AFC", StringComparison.OrdinalIgnoreCase) == true));

            if (!hasAfcAccess)
            {
                issues.Add(new AuditIssue
                {
                    Type = "MGMT_MISSING_AFC_ACCESS",
                    Severity = AuditSeverity.Info,
                    Message = $"Isolated management network '{mgmtNetwork.Name}' may lack AFC traffic access",
                    CurrentNetwork = mgmtNetwork.Name,
                    CurrentVlan = mgmtNetwork.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", mgmtNetwork.Name },
                        { "vlan", mgmtNetwork.VlanId },
                        { "required_domains", "afcapi.qcs.qualcomm.com, location.qcs.qualcomm.com, api.qcs.qualcomm.com" }
                    },
                    RuleId = "FW-MGMT-002",
                    ScoreImpact = 0,
                    RecommendedAction = "Add firewall rule allowing AFC traffic for 6GHz WiFi coordination"
                });
            }

            // Check for 5G modem registration traffic rule (only if a 5G/LTE device is present)
            if (has5GDevice)
            {
                var has5GModemAccess = rules.Any(r =>
                    r.Enabled &&
                    r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                    (r.Name?.Contains("5G", StringComparison.OrdinalIgnoreCase) == true ||
                     r.Name?.Contains("modem", StringComparison.OrdinalIgnoreCase) == true ||
                     r.Name?.Contains("LTE", StringComparison.OrdinalIgnoreCase) == true));

                if (!has5GModemAccess)
                {
                    issues.Add(new AuditIssue
                    {
                        Type = "MGMT_MISSING_5G_ACCESS",
                        Severity = AuditSeverity.Info,
                        Message = $"Isolated management network '{mgmtNetwork.Name}' may lack 5G/LTE modem registration access",
                        CurrentNetwork = mgmtNetwork.Name,
                        CurrentVlan = mgmtNetwork.VlanId,
                        Metadata = new Dictionary<string, object>
                        {
                            { "network", mgmtNetwork.Name },
                            { "vlan", mgmtNetwork.VlanId }
                        },
                        RuleId = "FW-MGMT-003",
                        ScoreImpact = 0,
                        RecommendedAction = "Add firewall rule allowing 5G/LTE modem registration traffic to the internet"
                    });
                }
            }
        }

        return issues;
    }
}

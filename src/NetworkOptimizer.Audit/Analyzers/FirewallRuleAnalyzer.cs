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
        var predefined = policy.GetBoolOrDefault("predefined", false);

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
            WebDomains = webDomains,
            Predefined = predefined
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
    /// Detect conflicting user-created firewall rules where order causes unexpected behavior.
    /// Only checks user-created rules (not predefined/system rules).
    /// - Info: DENY before ALLOW makes the ALLOW ineffective
    /// - Warning: ALLOW before DENY subverts a security rule
    /// </summary>
    public List<AuditIssue> DetectShadowedRules(List<FirewallRule> rules)
    {
        var issues = new List<AuditIssue>();

        // Only check user-created rules (skip predefined/system rules)
        var userRules = rules.Where(r => !r.Predefined && r.Enabled).ToList();

        // Group by ruleset
        var rulesets = userRules.GroupBy(r => r.Ruleset ?? "default");

        foreach (var ruleset in rulesets)
        {
            var orderedRules = ruleset.OrderBy(r => r.Index).ToList();

            for (int i = 0; i < orderedRules.Count; i++)
            {
                var laterRule = orderedRules[i];

                // Check if any earlier rule conflicts with this one
                for (int j = 0; j < i; j++)
                {
                    var earlierRule = orderedRules[j];

                    // Only care about conflicting actions (ALLOW vs DENY/BLOCK/DROP)
                    var earlierIsAllow = IsAllowAction(earlierRule.Action);
                    var laterIsAllow = IsAllowAction(laterRule.Action);

                    // Skip if same action type (both allow or both deny)
                    if (earlierIsAllow == laterIsAllow)
                        continue;

                    // Check if rules could overlap (same source/dest/protocol patterns)
                    if (!RulesCouldOverlap(earlierRule, laterRule))
                        continue;

                    if (earlierIsAllow && !laterIsAllow)
                    {
                        // Earlier ALLOW subverts later DENY - this is a potential security issue
                        issues.Add(new AuditIssue
                        {
                            Type = "ALLOW_SUBVERTS_DENY",
                            Severity = AuditSeverity.Recommended,
                            Message = $"Allow rule '{earlierRule.Name}' may subvert deny rule '{laterRule.Name}'",
                            Metadata = new Dictionary<string, object>
                            {
                                { "allow_rule", earlierRule.Name ?? earlierRule.Id },
                                { "allow_index", earlierRule.Index },
                                { "deny_rule", laterRule.Name ?? laterRule.Id },
                                { "deny_index", laterRule.Index }
                            },
                            RuleId = "FW-SUBVERT-001",
                            ScoreImpact = 5,
                            RecommendedAction = "Review rule order - the deny rule may never match due to the earlier allow rule"
                        });
                        break;
                    }
                    else if (!earlierIsAllow && laterIsAllow)
                    {
                        // Earlier DENY makes later ALLOW ineffective - informational
                        issues.Add(new AuditIssue
                        {
                            Type = "DENY_SHADOWS_ALLOW",
                            Severity = AuditSeverity.Info,
                            Message = $"Allow rule '{laterRule.Name}' may be ineffective due to earlier deny rule '{earlierRule.Name}'",
                            Metadata = new Dictionary<string, object>
                            {
                                { "allow_rule", laterRule.Name ?? laterRule.Id },
                                { "allow_index", laterRule.Index },
                                { "deny_rule", earlierRule.Name ?? earlierRule.Id },
                                { "deny_index", earlierRule.Index }
                            },
                            RuleId = "FW-SHADOW-001",
                            ScoreImpact = 0,
                            RecommendedAction = "Review rule order - the allow rule may never match due to the earlier deny rule"
                        });
                        break;
                    }
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Check if an action is an allow/accept action
    /// </summary>
    private static bool IsAllowAction(string? action)
    {
        if (string.IsNullOrEmpty(action))
            return false;
        return action.Equals("allow", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("accept", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if two rules could potentially overlap (match same traffic)
    /// </summary>
    private static bool RulesCouldOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        // If either rule is any->any for source/dest, they could overlap
        var rule1AnySource = string.IsNullOrEmpty(rule1.Source) && (rule1.SourceNetworkIds == null || !rule1.SourceNetworkIds.Any());
        var rule1AnyDest = string.IsNullOrEmpty(rule1.Destination) && (rule1.WebDomains == null || !rule1.WebDomains.Any());
        var rule2AnySource = string.IsNullOrEmpty(rule2.Source) && (rule2.SourceNetworkIds == null || !rule2.SourceNetworkIds.Any());
        var rule2AnyDest = string.IsNullOrEmpty(rule2.Destination) && (rule2.WebDomains == null || !rule2.WebDomains.Any());

        // If first rule is very broad (any source or any dest), it could shadow more specific rules
        if (rule1AnySource || rule1AnyDest)
            return true;

        // Check for matching source networks
        if (rule1.SourceNetworkIds != null && rule2.SourceNetworkIds != null)
        {
            if (rule1.SourceNetworkIds.Intersect(rule2.SourceNetworkIds).Any())
                return true;
        }

        // Check for matching sources
        if (!string.IsNullOrEmpty(rule1.Source) && rule1.Source == rule2.Source)
            return true;

        // Check for matching destinations
        if (!string.IsNullOrEmpty(rule1.Destination) && rule1.Destination == rule2.Destination)
            return true;

        // Check for matching web domains
        if (rule1.WebDomains != null && rule2.WebDomains != null)
        {
            if (rule1.WebDomains.Intersect(rule2.WebDomains, StringComparer.OrdinalIgnoreCase).Any())
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
    /// Check for missing inter-VLAN isolation rules.
    /// Networks with NetworkIsolationEnabled are already isolated by the system "Isolated Networks" rule.
    /// </summary>
    public List<AuditIssue> CheckInterVlanIsolation(List<FirewallRule> rules, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        // Find networks that should be isolated but DON'T have network_isolation_enabled
        // (networks with isolation enabled are handled by UniFi's built-in "Isolated Networks" rule)
        var iotNetworks = networks.Where(n => n.Purpose == NetworkPurpose.IoT && !n.NetworkIsolationEnabled).ToList();
        var guestNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Guest && !n.NetworkIsolationEnabled).ToList();
        var corporateNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Corporate).ToList();

        // Check if there are explicit deny rules between non-isolated IoT and Corporate
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
                            { "recommendation", "Enable network isolation or add firewall rule to block inter-VLAN traffic" }
                        },
                        RuleId = "FW-ISOLATION-001",
                        ScoreImpact = 7
                    });
                }
            }
        }

        // Similar check for non-isolated Guest networks
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
                            { "recommendation", "Enable network isolation or add firewall rule to block inter-VLAN traffic" }
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

            // Check for UniFi cloud access rule (config-based only)
            // Must have: source = management network, destination web domain = ui.com
            var hasUniFiAccess = rules.Any(r =>
                r.Enabled &&
                r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                r.SourceNetworkIds?.Contains(mgmtNetwork.Id) == true &&
                r.WebDomains?.Any(d => d.Contains("ui.com", StringComparison.OrdinalIgnoreCase)) == true);

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
            // Must have: source = management network, destination web domain = qcs.qualcomm.com
            var hasAfcAccess = rules.Any(r =>
                r.Enabled &&
                r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                r.SourceNetworkIds?.Contains(mgmtNetwork.Id) == true &&
                r.WebDomains?.Any(d => d.Contains("qcs.qualcomm.com", StringComparison.OrdinalIgnoreCase)) == true);

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

            // Check for NTP access rule - needed for time sync (required for AFC)
            // Can be satisfied by: web domain containing ntp.org OR destination port 123
            var hasNtpAccess = rules.Any(r =>
                r.Enabled &&
                r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                r.SourceNetworkIds?.Contains(mgmtNetwork.Id) == true &&
                (r.WebDomains?.Any(d => d.Contains("ntp.org", StringComparison.OrdinalIgnoreCase)) == true ||
                 r.DestinationPort == "123" ||
                 r.DestinationPort?.Contains("123") == true));

            if (!hasNtpAccess)
            {
                issues.Add(new AuditIssue
                {
                    Type = "MGMT_MISSING_NTP_ACCESS",
                    Severity = AuditSeverity.Info,
                    Message = $"Isolated management network '{mgmtNetwork.Name}' may lack NTP time sync access",
                    CurrentNetwork = mgmtNetwork.Name,
                    CurrentVlan = mgmtNetwork.VlanId,
                    Metadata = new Dictionary<string, object>
                    {
                        { "network", mgmtNetwork.Name },
                        { "vlan", mgmtNetwork.VlanId },
                        { "required_access", "ntp.org domain or UDP port 123" }
                    },
                    RuleId = "FW-MGMT-004",
                    ScoreImpact = 0,
                    RecommendedAction = "Add firewall rule allowing NTP traffic (UDP port 123 or ntp.org domain)"
                });
            }

            // Check for 5G/LTE modem registration traffic rule (only if a 5G/LTE device is present)
            // Must have: source = management network, destination web domains include carrier registration domains
            // Known domains: trafficmanager.net, t-mobile.com, gsma.com (carrier-specific domains may vary)
            if (has5GDevice)
            {
                var has5GModemAccess = rules.Any(r =>
                    r.Enabled &&
                    r.Action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true &&
                    r.SourceNetworkIds?.Contains(mgmtNetwork.Id) == true &&
                    r.WebDomains?.Any(d =>
                        d.Contains("trafficmanager.net", StringComparison.OrdinalIgnoreCase) ||
                        d.Contains("t-mobile.com", StringComparison.OrdinalIgnoreCase) ||
                        d.Contains("gsma.com", StringComparison.OrdinalIgnoreCase)) == true);

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
                            { "vlan", mgmtNetwork.VlanId },
                            { "required_domains", "trafficmanager.net, t-mobile.com, gsma.com" }
                        },
                        RuleId = "FW-MGMT-003",
                        ScoreImpact = 0,
                        RecommendedAction = "Add firewall rule allowing 5G/LTE modem registration traffic (trafficmanager.net, t-mobile.com, gsma.com)"
                    });
                }
            }
        }

        return issues;
    }
}

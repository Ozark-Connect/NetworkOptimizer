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
    private readonly FirewallRuleParser _parser;

    public FirewallRuleAnalyzer(ILogger<FirewallRuleAnalyzer> logger, FirewallRuleParser parser)
    {
        _logger = logger;
        _parser = parser;
    }

    /// <summary>
    /// Extract firewall rules from UniFi device JSON (delegates to parser)
    /// </summary>
    public List<FirewallRule> ExtractFirewallRules(JsonElement deviceData)
        => _parser.ExtractFirewallRules(deviceData);

    /// <summary>
    /// Extract firewall rules from UniFi firewall policies API response (delegates to parser)
    /// </summary>
    public List<FirewallRule> ExtractFirewallPolicies(JsonElement? firewallPoliciesData)
        => _parser.ExtractFirewallPolicies(firewallPoliciesData);

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
                    if (!FirewallRuleOverlapDetector.RulesOverlap(earlierRule, laterRule))
                        continue;

                    if (earlierIsAllow && !laterIsAllow)
                    {
                        // Earlier ALLOW subverts later DENY
                        // Check if this is a "narrow exception before broad deny" pattern
                        var isNarrowException = FirewallRuleOverlapDetector.IsNarrowerScope(earlierRule, laterRule);

                        if (isNarrowException)
                        {
                            // Narrow allow before broad deny = intentional exception pattern (Info only)
                            issues.Add(new AuditIssue
                            {
                                Type = "ALLOW_EXCEPTION_PATTERN",
                                Severity = AuditSeverity.Info,
                                Message = $"Allow rule '{earlierRule.Name}' creates an intentional exception to deny rule '{laterRule.Name}'",
                                Metadata = new Dictionary<string, object>
                                {
                                    { "allow_rule", earlierRule.Name ?? earlierRule.Id },
                                    { "allow_index", earlierRule.Index },
                                    { "deny_rule", laterRule.Name ?? laterRule.Id },
                                    { "deny_index", laterRule.Index },
                                    { "pattern", "narrow_exception" }
                                },
                                RuleId = "FW-EXCEPTION-001",
                                ScoreImpact = 0,
                                RecommendedAction = "This appears to be a deliberate exception pattern - no action required"
                            });
                        }
                        else
                        {
                            // Broad or similar scope allow before deny = potential security issue
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
                        }
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

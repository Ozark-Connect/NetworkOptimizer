using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;

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
    /// Set firewall groups for flattening port_group_id and ip_group_id references.
    /// Call this before ExtractFirewallPolicies to enable group resolution.
    /// </summary>
    public void SetFirewallGroups(IEnumerable<UniFiFirewallGroup>? groups)
        => _parser.SetFirewallGroups(groups);

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
    public List<AuditIssue> DetectShadowedRules(List<FirewallRule> rules, List<UniFiNetworkConfig>? networkConfigs = null)
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
                    var earlierIsAllow = earlierRule.ActionType.IsAllowAction();
                    var laterIsAllow = laterRule.ActionType.IsAllowAction();

                    // Skip if same action type (both allow or both deny)
                    if (earlierIsAllow == laterIsAllow)
                        continue;

                    // Check if rules could overlap (same source/dest/protocol patterns)
                    if (!FirewallRuleOverlapDetector.RulesOverlap(earlierRule, laterRule, networkConfigs))
                        continue;

                    if (earlierIsAllow && !laterIsAllow)
                    {
                        // Earlier ALLOW subverts later DENY
                        // Check if this is a "narrow exception before broad deny" pattern
                        var isNarrowException = FirewallRuleOverlapDetector.IsNarrowerScope(earlierRule, laterRule);

                        if (isNarrowException)
                        {
                            // Determine traffic pattern description for grouping
                            var description = GetExceptionPatternDescription(laterRule);

                            // Narrow allow before broad deny = intentional exception pattern (Info only)
                            issues.Add(new AuditIssue
                            {
                                Type = IssueTypes.AllowExceptionPattern,
                                Severity = AuditSeverity.Informational,
                                Message = $"Allow rule '{earlierRule.Name}' creates an intentional exception to deny rule '{laterRule.Name}'",
                                Description = description,
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
                                Type = IssueTypes.AllowSubvertsDeny,
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
                            // For subverts, only report the first one
                            break;
                        }
                        // For exception patterns, continue to find all of them
                    }
                    else if (!earlierIsAllow && laterIsAllow)
                    {
                        // Earlier DENY before later ALLOW
                        // Check if the deny is narrower than the allow in ANY dimension
                        // If deny has more specific criteria, it's a partial restriction, not full shadow
                        var isDenyNarrower = FirewallRuleOverlapDetector.IsNarrowerScope(earlierRule, laterRule);
                        var denyHasSpecificPort = !string.IsNullOrEmpty(earlierRule.DestinationPort) &&
                                                  string.IsNullOrEmpty(laterRule.DestinationPort);
                        var denyHasSpecificProtocol = earlierRule.Protocol != "all" &&
                                                      (laterRule.Protocol == "all" || string.IsNullOrEmpty(laterRule.Protocol));

                        if (isDenyNarrower || denyHasSpecificPort || denyHasSpecificProtocol)
                        {
                            // Deny is more specific in some dimension = partial restriction
                            // Example: Block UDP port 53 before Allow all traffic - only DNS is blocked
                            // This is usually intentional and not worth flagging
                            _logger.LogDebug(
                                "Skipping partial restriction: deny '{Deny}' is more specific than allow '{Allow}' " +
                                "(narrower={Narrower}, specificPort={Port}, specificProtocol={Protocol})",
                                earlierRule.Name, laterRule.Name, isDenyNarrower, denyHasSpecificPort, denyHasSpecificProtocol);
                        }
                        else
                        {
                            // Broad deny before allow = the allow may truly be ineffective
                            issues.Add(new AuditIssue
                            {
                                Type = IssueTypes.DenyShadowsAllow,
                                Severity = AuditSeverity.Informational,
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
                        }
                        // Continue checking other earlier rules that may also shadow this allow
                    }
                }
            }
        }

        return issues;
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

            // Skip predefined/system rules - these are UniFi built-in rules that users can't change
            // Includes "Allow All Traffic", "Allow Return Traffic", auto-generated "(Return)" rules, etc.
            if (rule.Predefined)
                continue;

            // Check for any->any rules
            // v2 API uses SourceMatchingTarget/DestinationMatchingTarget = "ANY"
            // Legacy API uses SourceType/DestinationType = "any" or empty Source/Destination
            var isAnySource = IsAnySource(rule);
            var isAnyDest = IsAnyDestination(rule);
            var isAnyProtocol = rule.Protocol?.Equals("all", StringComparison.OrdinalIgnoreCase) == true
                || string.IsNullOrEmpty(rule.Protocol);

            if (isAnySource && isAnyDest && isAnyProtocol && rule.ActionType.IsAllowAction())
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.PermissiveRule,
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
            // But don't flag if the rule has other restrictions that make it specific:
            // - Specific destination ports limit what can be accessed
            // - Specific source IPs limit who can access
            // - Web domains limit destination to specific sites
            else if ((isAnySource || isAnyDest) && rule.ActionType.IsAllowAction())
            {
                var hasSpecificPorts = !string.IsNullOrEmpty(rule.DestinationPort);
                var hasSpecificSourceIps = rule.SourceIps?.Any() == true;
                var hasWebDomains = rule.WebDomains?.Any() == true;

                // If ANY destination but has specific ports or source IPs, it's not truly "broad"
                if (isAnyDest && (hasSpecificPorts || hasSpecificSourceIps || hasWebDomains))
                    continue;

                // If ANY source but has specific destination ports or web domains, it's not truly "broad"
                if (isAnySource && (hasSpecificPorts || hasWebDomains))
                    continue;

                var direction = isAnySource ? "any source" : "any destination";
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.BroadRule,
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
                        Type = IssueTypes.OrphanedRule,
                        Severity = AuditSeverity.Informational,
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
                        Type = IssueTypes.OrphanedRule,
                        Severity = AuditSeverity.Informational,
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
    /// UniFi Guest networks (purpose="guest") have implicit isolation at switch/AP level.
    /// </summary>
    public List<AuditIssue> CheckInterVlanIsolation(List<FirewallRule> rules, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        // Find networks by purpose (only those without system isolation enabled need manual firewall rules)
        // UniFi Guest networks have implicit isolation at switch/AP level, so skip them too
        var iotNetworks = networks.Where(n => n.Purpose == NetworkPurpose.IoT && !n.NetworkIsolationEnabled).ToList();
        var guestNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Guest && !n.NetworkIsolationEnabled && !n.IsUniFiGuestNetwork).ToList();
        var securityNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Security && !n.NetworkIsolationEnabled).ToList();

        // Trusted networks that untrusted networks should be isolated FROM
        var corporateNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Corporate).ToList();
        var homeNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Home).ToList();
        var managementNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Management).ToList();

        // Combine trusted networks for easier iteration
        var trustedNetworks = corporateNetworks.Concat(homeNetworks).Concat(managementNetworks).ToList();

        // IoT should be isolated from: Corporate, Home, Management, Security
        foreach (var iot in iotNetworks)
        {
            // Check against trusted networks
            foreach (var trusted in trustedNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, iot, trusted, "FW-ISOLATION-IOT");
            }

            // IoT should also be isolated from Security (cameras)
            foreach (var security in securityNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, iot, security, "FW-ISOLATION-IOT-SEC");
            }
        }

        // Guest should be isolated from: Corporate, Home, Management, Security, IoT
        foreach (var guest in guestNetworks)
        {
            // Check against trusted networks
            foreach (var trusted in trustedNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, guest, trusted, "FW-ISOLATION-GUEST");
            }

            // Guest should be isolated from Security (cameras)
            foreach (var security in securityNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, guest, security, "FW-ISOLATION-GUEST-SEC");
            }

            // Guest should be isolated from IoT (guests shouldn't control smart home devices)
            foreach (var iot in iotNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, guest, iot, "FW-ISOLATION-GUEST-IOT");
            }
        }

        // Management should be isolated from: Corporate, Home, Security
        // Management networks should only be accessible to specific admin devices, not entire networks
        foreach (var mgmt in managementNetworks.Where(n => !n.NetworkIsolationEnabled))
        {
            foreach (var corp in corporateNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, corp, mgmt, "FW-ISOLATION-MGMT");
            }
            foreach (var home in homeNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, home, mgmt, "FW-ISOLATION-MGMT");
            }
            foreach (var security in securityNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, security, mgmt, "FW-ISOLATION-SEC-MGMT");
            }
        }

        // Now check for ALLOW rules between networks that should be isolated
        // This catches rules that explicitly open up traffic between isolated network types
        var allIotNetworks = networks.Where(n => n.Purpose == NetworkPurpose.IoT).ToList();
        var allGuestNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Guest).ToList();
        var allSecurityNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Security).ToList();

        // Check for allow rules between IoT and trusted/security networks
        foreach (var iot in allIotNetworks)
        {
            foreach (var trusted in trustedNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, iot, trusted);
            }
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, iot, security);
            }
        }

        // Check for allow rules between Guest and trusted/security/IoT networks
        foreach (var guest in allGuestNetworks)
        {
            foreach (var trusted in trustedNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, trusted);
            }
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, security);
            }
            foreach (var iot in allIotNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, iot);
            }
        }

        // Check for allow rules between Corporate/Home/Security and Management
        foreach (var mgmt in managementNetworks)
        {
            foreach (var corp in corporateNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, corp, mgmt);
            }
            foreach (var home in homeNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, home, mgmt);
            }
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, security, mgmt);
            }
        }

        return issues;
    }

    /// <summary>
    /// Helper to find and flag ALLOW rules between networks that should be isolated
    /// </summary>
    private void CheckForProblematicAllowRules(
        List<AuditIssue> issues,
        List<FirewallRule> rules,
        NetworkInfo network1,
        NetworkInfo network2)
    {
        // Don't check network against itself
        if (network1.Id == network2.Id)
            return;

        // Find all ALLOW rules between these two networks (either direction)
        var allowRules = rules.Where(r =>
            r.Enabled &&
            !r.Predefined &&
            r.ActionType.IsAllowAction() &&
            (HasNetworkPair(r, network1.Id, network2.Id) || HasNetworkPair(r, network2.Id, network1.Id)))
            .ToList();

        foreach (var rule in allowRules)
        {
            // Determine direction for the message
            var isForward = HasNetworkPair(rule, network1.Id, network2.Id);
            var sourceNet = isForward ? network1 : network2;
            var destNet = isForward ? network2 : network1;

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.IsolationBypassed,
                Severity = AuditSeverity.Critical,
                Message = $"Rule '{rule.Name}' allows traffic from {sourceNet.Name} ({sourceNet.Purpose}) to {destNet.Name} ({destNet.Purpose}) which should be isolated",
                Metadata = new Dictionary<string, object>
                {
                    { "rule_name", rule.Name ?? rule.Id },
                    { "rule_index", rule.Index },
                    { "source_network", sourceNet.Name },
                    { "source_purpose", sourceNet.Purpose.ToString() },
                    { "dest_network", destNet.Name },
                    { "dest_purpose", destNet.Purpose.ToString() },
                    { "recommendation", "Delete this rule or restrict to specific ports/protocols if necessary" }
                },
                RuleId = "FW-ISOLATION-BYPASS",
                ScoreImpact = 12
            });
        }
    }

    /// <summary>
    /// Helper to check for isolation rule between two networks and add issue if missing
    /// </summary>
    private void CheckAndAddIsolationIssue(
        List<AuditIssue> issues,
        List<FirewallRule> rules,
        NetworkInfo network1,
        NetworkInfo network2,
        string ruleIdPrefix)
    {
        // Don't check network against itself
        if (network1.Id == network2.Id)
            return;

        var hasIsolationRule = rules.Any(r =>
            r.Enabled &&
            r.ActionType.IsBlockAction() &&
            (HasNetworkPair(r, network1.Id, network2.Id) || HasNetworkPair(r, network2.Id, network1.Id)));

        if (!hasIsolationRule)
        {
            // Determine severity based on network types
            // Critical: Guest to sensitive networks, IoT to Management
            var isCritical = IsCriticalIsolationMissing(network1.Purpose, network2.Purpose);
            var severity = isCritical ? AuditSeverity.Critical : AuditSeverity.Recommended;
            var scoreImpact = isCritical ? 12 : 7;

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.MissingIsolation,
                Severity = severity,
                Message = $"No explicit isolation rule between {network1.Name} ({network1.Purpose}) and {network2.Name} ({network2.Purpose})",
                Metadata = new Dictionary<string, object>
                {
                    { "network1", network1.Name },
                    { "network1Purpose", network1.Purpose.ToString() },
                    { "network2", network2.Name },
                    { "network2Purpose", network2.Purpose.ToString() },
                    { "recommendation", "Enable network isolation or add firewall rule to block inter-VLAN traffic" }
                },
                RuleId = ruleIdPrefix,
                ScoreImpact = scoreImpact
            });
        }
    }

    /// <summary>
    /// Determines if missing isolation between two network types is critical.
    /// Guest accessing sensitive networks, and anything accessing Management are critical.
    /// </summary>
    private static bool IsCriticalIsolationMissing(NetworkPurpose purpose1, NetworkPurpose purpose2)
    {
        // Guest to Corporate, Management, or Security = Critical
        if (purpose1 == NetworkPurpose.Guest || purpose2 == NetworkPurpose.Guest)
        {
            var other = purpose1 == NetworkPurpose.Guest ? purpose2 : purpose1;
            if (other is NetworkPurpose.Corporate or NetworkPurpose.Management or NetworkPurpose.Security)
                return true;
        }

        // Anything to Management = Critical (Management should only be accessed by specific admin devices)
        // This includes: IoT, Corporate, Home, Security
        if (purpose1 == NetworkPurpose.Management || purpose2 == NetworkPurpose.Management)
        {
            var other = purpose1 == NetworkPurpose.Management ? purpose2 : purpose1;
            if (other is NetworkPurpose.IoT or NetworkPurpose.Corporate or NetworkPurpose.Home or NetworkPurpose.Security)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Run all firewall analyses
    /// </summary>
    public List<AuditIssue> AnalyzeFirewallRules(List<FirewallRule> rules, List<NetworkInfo> networks, List<UniFiNetworkConfig>? networkConfigs = null)
    {
        var issues = new List<AuditIssue>();

        _logger.LogInformation("Analyzing {RuleCount} firewall rules", rules.Count);

        issues.AddRange(DetectShadowedRules(rules, networkConfigs));
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
    /// <param name="rules">Firewall rules to analyze</param>
    /// <param name="networks">Network configurations</param>
    /// <param name="has5GDevice">Whether a 5G/LTE device is present on the network</param>
    /// <param name="externalZoneId">Optional External/WAN zone ID for validating port-based rule destinations</param>
    public List<AuditIssue> AnalyzeManagementNetworkFirewallAccess(List<FirewallRule> rules, List<NetworkInfo> networks, bool has5GDevice = false, string? externalZoneId = null)
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
            // Must have: source = management network, destination web domain = ui.com, TCP allowed
            var hasUniFiAccess = rules.Any(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                r.WebDomains?.Any(d => d.Contains("ui.com", StringComparison.OrdinalIgnoreCase)) == true &&
                FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp"));

            if (!hasUniFiAccess)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.MgmtMissingUnifiAccess,
                    Severity = AuditSeverity.Informational,
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
            // Must have: source = management network, destination web domain = qcs.qualcomm.com, TCP allowed
            var hasAfcAccess = rules.Any(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                r.WebDomains?.Any(d => d.Contains("qcs.qualcomm.com", StringComparison.OrdinalIgnoreCase)) == true &&
                FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp"));

            if (!hasAfcAccess)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.MgmtMissingAfcAccess,
                    Severity = AuditSeverity.Informational,
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
            // Can be satisfied by: web domain containing ntp.org (TCP) OR destination port 123 (UDP) to External zone
            // Note: Must check that ports and protocol are not inverted
            // For port-based rules, we must verify the destination zone is External
            var hasNtpAccess = rules.Any(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                ((r.WebDomains?.Any(d => d.Contains("ntp.org", StringComparison.OrdinalIgnoreCase)) == true &&
                  FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp")) ||
                 (FirewallGroupHelper.RuleAllowsPortAndProtocol(r, "123", "udp") &&
                  TargetsExternalZone(r, externalZoneId))));

            if (!hasNtpAccess)
            {
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.MgmtMissingNtpAccess,
                    Severity = AuditSeverity.Informational,
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
            // Known carrier domains - add more as we discover them for different carriers:
            // - T-Mobile: trafficmanager.net, t-mobile.com
            // - Generic: gsma.com (used by multiple carriers)
            if (has5GDevice)
            {
                var has5GModemAccess = rules.Any(r =>
                    r.Enabled &&
                    r.ActionType.IsAllowAction() &&
                    AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                    r.WebDomains?.Any(d =>
                        d.Contains("trafficmanager.net", StringComparison.OrdinalIgnoreCase) ||
                        d.Contains("t-mobile.com", StringComparison.OrdinalIgnoreCase) ||
                        d.Contains("gsma.com", StringComparison.OrdinalIgnoreCase)) == true &&
                    FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp"));

                if (!has5GModemAccess)
                {
                    issues.Add(new AuditIssue
                    {
                        Type = IssueTypes.MgmtMissing5gAccess,
                        Severity = AuditSeverity.Informational,
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

    /// <summary>
    /// Check if a firewall rule has "any" source (matches all sources)
    /// Handles both v2 API format (SourceMatchingTarget) and legacy format (SourceType/Source)
    /// </summary>
    private static bool IsAnySource(FirewallRule rule)
    {
        // v2 API format: SourceMatchingTarget explicitly tells us the matching type
        if (!string.IsNullOrEmpty(rule.SourceMatchingTarget))
        {
            return rule.SourceMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase);
        }

        // Legacy format: check SourceType or fall back to empty Source
        return rule.SourceType?.Equals("any", StringComparison.OrdinalIgnoreCase) == true
            || string.IsNullOrEmpty(rule.Source);
    }

    /// <summary>
    /// Check if a firewall rule has "any" destination (matches all destinations)
    /// Handles both v2 API format (DestinationMatchingTarget) and legacy format (DestinationType/Destination)
    /// </summary>
    private static bool IsAnyDestination(FirewallRule rule)
    {
        // v2 API format: DestinationMatchingTarget explicitly tells us the matching type
        if (!string.IsNullOrEmpty(rule.DestinationMatchingTarget))
        {
            return rule.DestinationMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase);
        }

        // Legacy format: check DestinationType or fall back to empty Destination
        return rule.DestinationType?.Equals("any", StringComparison.OrdinalIgnoreCase) == true
            || string.IsNullOrEmpty(rule.Destination);
    }

    /// <summary>
    /// Check if a firewall rule applies to traffic from a specific source network.
    /// Handles v2 API format (SourceNetworkIds + SourceMatchOppositeNetworks) and legacy format (Source).
    /// </summary>
    /// <param name="rule">The firewall rule to check</param>
    /// <param name="networkId">The network ID to check against</param>
    /// <returns>True if the rule applies to traffic from the specified network</returns>
    private static bool AppliesToSourceNetwork(FirewallRule rule, string networkId)
    {
        // v2 API: Check SourceMatchingTarget first
        if (!string.IsNullOrEmpty(rule.SourceMatchingTarget))
        {
            if (rule.SourceMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase))
            {
                return true; // Matches all networks
            }

            if (rule.SourceMatchingTarget.Equals("NETWORK", StringComparison.OrdinalIgnoreCase))
            {
                var networkIds = rule.SourceNetworkIds ?? new List<string>();
                if (rule.SourceMatchOppositeNetworks)
                {
                    // Match Opposite: rule applies to all networks EXCEPT those listed
                    return !networkIds.Contains(networkId);
                }
                else
                {
                    // Normal: rule applies ONLY to networks listed
                    return networkIds.Contains(networkId);
                }
            }

            // For IP, CLIENT, etc. - doesn't match by network ID
            return false;
        }

        // Backward compatibility: if SourceMatchingTarget is not set but SourceNetworkIds is populated,
        // check the network IDs (this handles rules created without explicit SourceMatchingTarget)
        if (rule.SourceNetworkIds != null && rule.SourceNetworkIds.Count > 0)
        {
            if (rule.SourceMatchOppositeNetworks)
            {
                return !rule.SourceNetworkIds.Contains(networkId);
            }
            return rule.SourceNetworkIds.Contains(networkId);
        }

        // Legacy format
        return rule.Source == networkId;
    }

    /// <summary>
    /// Check if a firewall rule applies to traffic to a specific destination network.
    /// Handles v2 API format (DestinationNetworkIds + DestinationMatchOppositeNetworks) and legacy format (Destination).
    /// </summary>
    /// <param name="rule">The firewall rule to check</param>
    /// <param name="networkId">The network ID to check against</param>
    /// <returns>True if the rule applies to traffic to the specified network</returns>
    private static bool AppliesToDestinationNetwork(FirewallRule rule, string networkId)
    {
        // v2 API: Check DestinationMatchingTarget first
        if (!string.IsNullOrEmpty(rule.DestinationMatchingTarget))
        {
            if (rule.DestinationMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase))
            {
                return true; // Matches all networks
            }

            if (rule.DestinationMatchingTarget.Equals("NETWORK", StringComparison.OrdinalIgnoreCase))
            {
                var networkIds = rule.DestinationNetworkIds ?? new List<string>();
                if (rule.DestinationMatchOppositeNetworks)
                {
                    // Match Opposite: rule applies to all networks EXCEPT those listed
                    return !networkIds.Contains(networkId);
                }
                else
                {
                    // Normal: rule applies ONLY to networks listed
                    return networkIds.Contains(networkId);
                }
            }

            // For IP, etc. - doesn't match by network ID
            return false;
        }

        // Backward compatibility: if DestinationMatchingTarget is not set but DestinationNetworkIds is populated,
        // check the network IDs (this handles rules created without explicit DestinationMatchingTarget)
        if (rule.DestinationNetworkIds != null && rule.DestinationNetworkIds.Count > 0)
        {
            if (rule.DestinationMatchOppositeNetworks)
            {
                return !rule.DestinationNetworkIds.Contains(networkId);
            }
            return rule.DestinationNetworkIds.Contains(networkId);
        }

        // Legacy format
        return rule.Destination == networkId;
    }

    /// <summary>
    /// Check if a firewall rule matches a specific source->destination network pair.
    /// Handles both v2 API format (with Match Opposite support) and legacy format.
    /// </summary>
    private static bool HasNetworkPair(FirewallRule rule, string sourceNetworkId, string destNetworkId)
    {
        return AppliesToSourceNetwork(rule, sourceNetworkId) && AppliesToDestinationNetwork(rule, destNetworkId);
    }

    /// <summary>
    /// Check if a firewall rule targets the External/WAN zone.
    /// Returns true if the rule's destination zone matches the external zone ID,
    /// or if we don't have an external zone ID to check against.
    /// </summary>
    private static bool TargetsExternalZone(FirewallRule rule, string? externalZoneId)
    {
        // If no external zone ID is provided, we can't validate - assume it targets external
        if (string.IsNullOrEmpty(externalZoneId))
            return true;

        // Check if the rule's destination zone matches the external zone
        return string.Equals(rule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines a description for firewall exception patterns based on the deny rule being excepted.
    /// Used for grouping similar exceptions in the UI.
    /// </summary>
    private static string GetExceptionPatternDescription(FirewallRule denyRule)
    {
        var destTarget = denyRule.DestinationMatchingTarget?.ToUpperInvariant();
        var srcTarget = denyRule.SourceMatchingTarget?.ToUpperInvariant();

        // Check for external/internet blocking rules (dest zone is external or dest is ANY with external zone)
        // These are typically "block internet" rules
        if (!string.IsNullOrEmpty(denyRule.DestinationZoneId) &&
            denyRule.DestinationZoneId != denyRule.SourceZoneId &&
            destTarget == "ANY")
        {
            return "External Access Exception";
        }

        // Check for inter-VLAN isolation rules (blocking network-to-network or any-to-network)
        if (destTarget == "NETWORK" || srcTarget == "NETWORK")
        {
            return "Cross-VLAN Access Exception";
        }

        // Default for other patterns
        return "Firewall Exception";
    }
}

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
    /// Parse a single firewall policy JSON element into a FirewallRule (delegates to parser)
    /// </summary>
    public FirewallRule? ParseFirewallPolicy(JsonElement policyElement)
        => _parser.ParseFirewallPolicy(policyElement);

    /// <summary>
    /// Detect conflicting user-created firewall rules where order causes unexpected behavior.
    /// Only checks user-created rules (not predefined/system rules).
    /// - Info: DENY before ALLOW makes the ALLOW ineffective
    /// - Warning: ALLOW before DENY subverts a security rule
    /// </summary>
    public List<AuditIssue> DetectShadowedRules(List<FirewallRule> rules, List<UniFiNetworkConfig>? networkConfigs = null, string? externalZoneId = null, List<NetworkInfo>? networks = null)
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
                            // Skip known management service exceptions - they're covered by MGMT_MISSING_* rules
                            if (IsKnownManagementServiceException(earlierRule))
                            {
                                _logger.LogDebug(
                                    "Skipping management service exception: '{AllowRule}' allows known service traffic",
                                    earlierRule.Name);
                                continue;
                            }

                            // Determine traffic pattern description for grouping
                            // Use the allow rule for destination purpose since it's more specific
                            var description = GetExceptionPatternDescription(laterRule, earlierRule, externalZoneId, networks);

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

                        // Check if deny has specific destinations while allow is broader
                        var allowDestTarget = laterRule.DestinationMatchingTarget?.ToUpperInvariant() ?? "ANY";
                        var denyHasSpecificDomains = earlierRule.WebDomains?.Count > 0 && allowDestTarget == "ANY";
                        var denyHasSpecificNetworks = earlierRule.DestinationNetworkIds?.Count > 0 && allowDestTarget == "ANY";
                        var denyHasSpecificIps = earlierRule.DestinationIps?.Count > 0 && allowDestTarget == "ANY";
                        var denyHasSpecificApps = earlierRule.AppIds?.Count > 0 && (laterRule.AppIds == null || laterRule.AppIds.Count == 0);
                        var denyHasSpecificAppCategories = earlierRule.AppCategoryIds?.Count > 0 && (laterRule.AppCategoryIds == null || laterRule.AppCategoryIds.Count == 0);
                        var denyHasSpecificDestination = denyHasSpecificDomains || denyHasSpecificNetworks || denyHasSpecificIps || denyHasSpecificApps || denyHasSpecificAppCategories;

                        if (isDenyNarrower || denyHasSpecificPort || denyHasSpecificProtocol || denyHasSpecificDestination)
                        {
                            // Deny is more specific in some dimension = partial restriction
                            // Example: Block specific domains before Allow all to External - only those domains are blocked
                            // This is usually intentional and not worth flagging
                            _logger.LogDebug(
                                "Skipping partial restriction: deny '{Deny}' is more specific than allow '{Allow}' " +
                                "(narrower={Narrower}, specificPort={Port}, specificProtocol={Protocol}, specificDest={Dest})",
                                earlierRule.Name, laterRule.Name, isDenyNarrower, denyHasSpecificPort, denyHasSpecificProtocol, denyHasSpecificDestination);
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
    /// <param name="rules">Firewall rules to analyze</param>
    /// <param name="networks">Network configurations</param>
    /// <param name="externalZoneId">External zone ID - rules targeting this zone are not inter-VLAN rules</param>
    public List<AuditIssue> CheckInterVlanIsolation(List<FirewallRule> rules, List<NetworkInfo> networks, string? externalZoneId = null)
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
                CheckForProblematicAllowRules(issues, rules, iot, trusted, externalZoneId);
            }
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, iot, security, externalZoneId);
            }
        }

        // Check for allow rules between Guest and trusted/security/IoT networks
        foreach (var guest in allGuestNetworks)
        {
            foreach (var trusted in trustedNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, trusted, externalZoneId);
            }
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, security, externalZoneId);
            }
            foreach (var iot in allIotNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, iot, externalZoneId);
            }
        }

        // Check for allow rules between Corporate/Home/Security and Management
        foreach (var mgmt in managementNetworks)
        {
            foreach (var corp in corporateNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, corp, mgmt, externalZoneId);
            }
            foreach (var home in homeNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, home, mgmt, externalZoneId);
            }
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, security, mgmt, externalZoneId);
            }
        }

        return issues;
    }

    /// <summary>
    /// Helper to find and flag ALLOW rules between networks that should be isolated.
    /// Rules targeting the External zone are skipped - they're for outbound internet access, not inter-VLAN traffic.
    /// </summary>
    private void CheckForProblematicAllowRules(
        List<AuditIssue> issues,
        List<FirewallRule> rules,
        NetworkInfo network1,
        NetworkInfo network2,
        string? externalZoneId)
    {
        // Don't check network against itself
        if (network1.Id == network2.Id)
            return;

        // Find all ALLOW rules between these two networks (either direction)
        // Skip rules that explicitly target the External zone - they're for outbound internet, not inter-VLAN
        var allowRules = rules.Where(r =>
            r.Enabled &&
            !r.Predefined &&
            r.ActionType.IsAllowAction() &&
            !IsExternalZoneRule(r, externalZoneId) &&
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
    /// Check for networks with internet disabled but that have allow rules permitting broad external access.
    /// This is a misconfiguration: the allow rule is ineffective because internet is blocked,
    /// but it suggests the user may have intended to allow internet access.
    /// </summary>
    public List<AuditIssue> CheckInternetDisabledBroadAllow(
        List<FirewallRule> rules,
        List<NetworkInfo> networks,
        string? externalZoneId)
    {
        var issues = new List<AuditIssue>();

        // Find networks where internet is disabled (via config or firewall rule)
        var internetDisabledNetworks = networks.Where(n =>
            !HasEffectiveInternetAccess(n, rules, externalZoneId)).ToList();

        if (!internetDisabledNetworks.Any())
        {
            return issues;
        }

        // HTTP/HTTPS app IDs that represent broad internet access
        // These are well-known app categories in UniFi
        var broadInternetAppIds = HttpAppIds.AllHttpAppIds;

        foreach (var network in internetDisabledNetworks)
        {
            // Find allow rules from this network that permit broad external access
            // Skip predefined/system rules (like "Allow Return Traffic")
            var broadAllowRules = rules.Where(rule =>
                rule.Enabled &&
                !rule.Predefined &&
                rule.ActionType.IsAllowAction() &&
                RuleAppliesToNetwork(rule, network) &&
                IsBroadExternalAccess(rule, externalZoneId, broadInternetAppIds)).ToList();

            foreach (var rule in broadAllowRules)
            {
                var accessType = GetBroadAccessDescription(rule, externalZoneId);
                issues.Add(new AuditIssue
                {
                    Type = "INTERNET_BLOCK_BYPASSED",
                    Severity = AuditSeverity.Recommended,
                    Message = $"Network '{network.Name}' has internet disabled but rule '{rule.Name}' allows {accessType}. " +
                              "This firewall rule circumvents the network's internet access restriction.",
                    Metadata = new Dictionary<string, object>
                    {
                        { "network_name", network.Name },
                        { "network_id", network.Id },
                        { "rule_name", rule.Name ?? rule.Id },
                        { "rule_id", rule.Id },
                        { "access_type", accessType }
                    },
                    ScoreImpact = 3
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Determines if a rule applies to a specific network (as source).
    /// Handles SourceMatchOppositeNetworks which inverts the matching.
    /// </summary>
    private static bool RuleAppliesToNetwork(FirewallRule rule, NetworkInfo network)
    {
        var sourceTarget = rule.SourceMatchingTarget?.ToUpperInvariant();

        // ANY source applies to all networks
        if (sourceTarget == "ANY" || string.IsNullOrEmpty(sourceTarget))
            return true;

        // NETWORK source - check if network is in the list
        if (sourceTarget == "NETWORK")
        {
            var isInList = rule.SourceNetworkIds?.Contains(network.Id) == true;

            // If SourceMatchOppositeNetworks is true, the rule applies to networks NOT in the list
            if (rule.SourceMatchOppositeNetworks)
                return !isInList;

            // Normal case: rule applies to networks IN the list
            return isInList;
        }

        return false;
    }

    /// <summary>
    /// Determines if an allow rule permits broad external/internet access (HTTP/HTTPS/QUIC).
    /// We only want to flag rules that allow general web traffic, not narrow rules like NTP or specific domains.
    /// </summary>
    private static bool IsBroadExternalAccess(
        FirewallRule rule,
        string? externalZoneId,
        HashSet<int> broadInternetAppIds)
    {
        // Rules with specific domains are narrow, not broad (e.g., UniFi cloud access)
        if (rule.WebDomains?.Count > 0)
            return false;

        // Rules with specific destination IPs are narrow
        if (rule.DestinationIps?.Count > 0)
            return false;

        // Rules with specific destination networks are narrow
        if (rule.DestinationNetworkIds?.Count > 0)
            return false;

        var destTarget = rule.DestinationMatchingTarget?.ToUpperInvariant();
        var protocol = rule.Protocol?.ToLowerInvariant() ?? "all";

        // Check for HTTP/HTTPS app IDs - these are always broad web access
        if (rule.AppIds != null && rule.AppIds.Any(id =>
            broadInternetAppIds.Contains(id)))
            return true;

        // Check for Web Services app category (13) - includes HTTP, HTTPS, and many web apps
        if (rule.AppCategoryIds != null && rule.AppCategoryIds.Any(HttpAppIds.IsWebCategory))
            return true;

        // Check for HTTP/HTTPS/QUIC ports with correct protocol combinations:
        // - HTTP: Port 80 + TCP (or All/TCP_UDP) - UDP port 80 is NOT HTTP
        // - HTTPS: Port 443 + TCP (or All/TCP_UDP)
        // - QUIC: Port 443 + UDP (or All/TCP_UDP)
        if (!string.IsNullOrEmpty(rule.DestinationPort))
        {
            var ports = ParsePorts(rule.DestinationPort);
            var includesTcp = protocol is "all" or "tcp" or "tcp_udp";
            var includesUdp = protocol is "all" or "udp" or "tcp_udp";

            // Port 80 requires TCP for HTTP
            if (ports.Contains(80) && includesTcp)
                return true;

            // Port 443 with TCP = HTTPS, with UDP = QUIC - both are broad web access
            if (ports.Contains(443) && (includesTcp || includesUdp))
                return true;

            // If rule has specific non-HTTP ports or wrong protocol, it's narrow
            return false;
        }

        // Check if destination is the External zone with ANY target and ALL protocols
        // This is truly broad access
        if (!string.IsNullOrEmpty(externalZoneId) &&
            string.Equals(rule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase))
        {
            if ((destTarget == "ANY" || string.IsNullOrEmpty(destTarget)) &&
                protocol == "all")
                return true;
        }

        // Check for ANY destination with all protocols (no zone specified)
        if ((destTarget == "ANY" || string.IsNullOrEmpty(destTarget)) &&
            protocol == "all" &&
            string.IsNullOrEmpty(rule.DestinationPort))
            return true;

        return false;
    }

    /// <summary>
    /// Parse port specification into a set of individual ports
    /// </summary>
    private static HashSet<int> ParsePorts(string portSpec)
    {
        var ports = new HashSet<int>();
        if (string.IsNullOrEmpty(portSpec))
            return ports;

        foreach (var part in portSpec.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                // Port range
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0], out var start) &&
                    int.TryParse(rangeParts[1], out var end))
                {
                    for (var port = start; port <= end; port++)
                        ports.Add(port);
                }
            }
            else if (int.TryParse(trimmed, out var port))
            {
                ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>
    /// Get a human-readable description of what broad access the rule permits
    /// </summary>
    private static string GetBroadAccessDescription(FirewallRule rule, string? externalZoneId)
    {
        var destTarget = rule.DestinationMatchingTarget?.ToUpperInvariant();

        if (!string.IsNullOrEmpty(externalZoneId) &&
            string.Equals(rule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return "external/internet access";
        }

        if (!string.IsNullOrEmpty(rule.DestinationPort))
        {
            var ports = ParsePorts(rule.DestinationPort);
            if (ports.Contains(80) && ports.Contains(443))
                return "HTTP/HTTPS access";
            if (ports.Contains(80))
                return "HTTP access";
            if (ports.Contains(443))
                return "HTTPS access";
        }

        if (destTarget == "ANY" || string.IsNullOrEmpty(destTarget))
            return "broad external access";

        return "external access";
    }

    /// <summary>
    /// Run all firewall analyses
    /// </summary>
    public List<AuditIssue> AnalyzeFirewallRules(List<FirewallRule> rules, List<NetworkInfo> networks, List<UniFiNetworkConfig>? networkConfigs = null, string? externalZoneId = null)
    {
        var issues = new List<AuditIssue>();

        _logger.LogInformation("Analyzing {RuleCount} firewall rules", rules.Count);

        issues.AddRange(DetectShadowedRules(rules, networkConfigs, externalZoneId, networks));
        issues.AddRange(DetectPermissiveRules(rules));
        issues.AddRange(DetectOrphanedRules(rules, networks));
        issues.AddRange(CheckInterVlanIsolation(rules, networks, externalZoneId));
        issues.AddRange(CheckInternetDisabledBroadAllow(rules, networks, externalZoneId));

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

        // Find management networks that are isolated and don't have effective internet access
        // Internet can be blocked via: 1) network config (InternetAccessEnabled=false), or
        // 2) a firewall rule blocking all traffic to the External zone
        var isolatedMgmtNetworks = networks.Where(n =>
            n.Purpose == NetworkPurpose.Management &&
            n.NetworkIsolationEnabled &&
            !HasEffectiveInternetAccess(n, rules, externalZoneId)).ToList();

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
            // NTP uses UDP port 123 - domain filtering doesn't help since NTP talks directly to IP addresses
            var hasNtpAccess = rules.Any(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                FirewallGroupHelper.RuleAllowsPortAndProtocol(r, "123", "udp") &&
                TargetsExternalZone(r, externalZoneId));

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
                        { "required_access", "UDP port 123 to External zone" }
                    },
                    RuleId = "FW-MGMT-004",
                    ScoreImpact = 0,
                    RecommendedAction = "Add firewall rule allowing NTP traffic (UDP port 123 to External zone)"
                });
            }

            // Check for 5G/LTE modem registration traffic rule (only if a 5G/LTE device is present)
            // The rule can target:
            // - The management network (modem is on management VLAN)
            // - A specific IP (modem's IP address)
            // - A specific MAC (modem's MAC address)
            // - ANY source (allows all devices including the modem)
            // Known carrier domains - add more as we discover them for different carriers:
            // - T-Mobile: trafficmanager.net, t-mobile.com
            // - Generic: gsma.com (used by multiple carriers)
            if (has5GDevice)
            {
                var has5GModemAccess = rules.Any(r =>
                    r.Enabled &&
                    r.ActionType.IsAllowAction() &&
                    Allows5GRegistrationDomains(r) &&
                    FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp") &&
                    // Source can be: management network, specific IP, specific MAC, or ANY
                    (AppliesToSourceNetwork(r, mgmtNetwork.Id) ||
                     IsSourceIpBased(r) ||
                     IsSourceMacBased(r) ||
                     IsAnySource(r)));

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
    /// Check if a firewall rule targets a specific IP address as the source
    /// </summary>
    private static bool IsSourceIpBased(FirewallRule rule)
    {
        return rule.SourceMatchingTarget?.Equals("IP", StringComparison.OrdinalIgnoreCase) == true
            && rule.SourceIps?.Count > 0;
    }

    /// <summary>
    /// Check if a firewall rule targets a specific MAC address (client) as the source
    /// </summary>
    private static bool IsSourceMacBased(FirewallRule rule)
    {
        return rule.SourceMatchingTarget?.Equals("CLIENT", StringComparison.OrdinalIgnoreCase) == true
            && rule.SourceClientMacs?.Count > 0;
    }

    /// <summary>
    /// Check if a firewall rule allows 5G/LTE modem registration domains
    /// Known carrier domains:
    /// - T-Mobile: trafficmanager.net, t-mobile.com
    /// - Generic: gsma.com (used by multiple carriers)
    /// </summary>
    private static bool Allows5GRegistrationDomains(FirewallRule rule)
    {
        return rule.WebDomains?.Any(d =>
            d.Contains("trafficmanager.net", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("t-mobile.com", StringComparison.OrdinalIgnoreCase) ||
            d.Contains("gsma.com", StringComparison.OrdinalIgnoreCase)) == true;
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
    /// Check if a firewall rule explicitly targets the External/WAN zone.
    /// Returns true only if we have an external zone ID AND the rule targets it.
    /// Returns false if no external zone ID is provided (conservative - don't skip the rule).
    /// </summary>
    private static bool IsExternalZoneRule(FirewallRule rule, string? externalZoneId)
    {
        // If no external zone ID is provided, we can't determine - return false (don't skip)
        if (string.IsNullOrEmpty(externalZoneId))
            return false;

        // Check if the rule's destination zone matches the external zone
        return string.Equals(rule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines if a network has effective internet access.
    /// Returns false if internet is blocked via either:
    /// 1. internet_access_enabled is false in network config, OR
    /// 2. A firewall rule blocks all traffic from this network to the External zone
    /// </summary>
    private bool HasEffectiveInternetAccess(
        NetworkInfo network,
        List<FirewallRule> firewallRules,
        string? externalZoneId)
    {
        // If internet access is disabled in network config, it's blocked
        if (!network.InternetAccessEnabled)
        {
            _logger.LogDebug("Network '{Name}' has internet_access_enabled=false", network.Name);
            return false;
        }

        // If no External zone detected, use the config setting
        if (string.IsNullOrEmpty(externalZoneId))
        {
            return network.InternetAccessEnabled;
        }

        // Check if there's a firewall rule that blocks internet access for this network
        if (IsInternetBlockedViaFirewall(network, firewallRules, externalZoneId))
        {
            _logger.LogDebug("Network '{Name}' has internet blocked via firewall rule", network.Name);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Check if a network has internet access blocked via a firewall rule.
    /// A rule blocks internet if ALL conditions are met:
    /// - Rule is enabled
    /// - Action is block/drop/reject/deny
    /// - Source matching target is "NETWORK" and network_ids contains this network's ID
    /// - Destination zone ID matches the External zone
    /// - Destination matching target is "ANY" (all destinations in the zone)
    /// - Protocol is "all" (blocks all traffic, not just specific ports)
    /// </summary>
    private bool IsInternetBlockedViaFirewall(
        NetworkInfo network,
        List<FirewallRule> firewallRules,
        string externalZoneId)
    {
        foreach (var rule in firewallRules)
        {
            // Rule must be enabled
            if (!rule.Enabled)
                continue;

            // Action must be a block action
            if (!rule.ActionType.IsBlockAction())
                continue;

            // Source must be NETWORK type with this network's ID in the list
            if (!string.Equals(rule.SourceMatchingTarget, "NETWORK", StringComparison.OrdinalIgnoreCase))
                continue;

            if (rule.SourceNetworkIds == null || !rule.SourceNetworkIds.Contains(network.Id))
                continue;

            // Destination zone must be the External zone
            if (!string.Equals(rule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Destination must target ANY (all destinations in the zone)
            if (!string.Equals(rule.DestinationMatchingTarget, "ANY", StringComparison.OrdinalIgnoreCase))
                continue;

            // Protocol must be "all" to block ALL traffic (not just specific ports/protocols)
            if (!string.Equals(rule.Protocol, "all", StringComparison.OrdinalIgnoreCase))
                continue;

            _logger.LogDebug(
                "Found firewall rule '{RuleName}' that blocks internet for network '{NetworkName}'",
                rule.Name, network.Name);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines a description for firewall exception patterns based on the deny rule being excepted.
    /// Uses the allow rule's destination for purpose lookup since it's more specific.
    /// </summary>
    private static string GetExceptionPatternDescription(FirewallRule denyRule, FirewallRule allowRule, string? externalZoneId, List<NetworkInfo>? networks)
    {
        var destTarget = denyRule.DestinationMatchingTarget?.ToUpperInvariant();
        var srcTarget = denyRule.SourceMatchingTarget?.ToUpperInvariant();

        // Check for external/internet blocking rules - must target the external zone specifically
        // If the destination zone is external, it's an external access exception regardless of
        // whether the destination is ANY, specific IPs, or domains
        if (!string.IsNullOrEmpty(externalZoneId) &&
            string.Equals(denyRule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return "External Access Exception";
        }

        // Check for inter-VLAN isolation rules (blocking network-to-network or any-to-network)
        // Group by the ALLOW rule's destination network purpose (more specific than deny rule)
        if (destTarget == "NETWORK" || srcTarget == "NETWORK")
        {
            // Try to get purpose from the allow rule's destination first (more specific)
            var purposeSuffix = GetDestinationNetworkPurposeSuffix(allowRule, networks);
            // Fall back to deny rule if allow rule doesn't have network destinations
            if (string.IsNullOrEmpty(purposeSuffix))
            {
                purposeSuffix = GetDestinationNetworkPurposeSuffix(denyRule, networks);
            }
            return $"Cross-VLAN Access Exception{purposeSuffix}";
        }

        // Default for other patterns (including Gateway zone blocks)
        return "Firewall Exception";
    }

    /// <summary>
    /// Gets a suffix describing the destination network purpose for grouping.
    /// Returns empty string if purpose can't be determined or there are multiple different purposes.
    /// </summary>
    private static string GetDestinationNetworkPurposeSuffix(FirewallRule rule, List<NetworkInfo>? networks)
    {
        if (networks == null || networks.Count == 0)
            return "";

        var purposes = new HashSet<NetworkPurpose>();

        // First try: Check DestinationNetworkIds
        var destNetworkIds = rule.DestinationNetworkIds;
        if (destNetworkIds != null && destNetworkIds.Count > 0)
        {
            foreach (var networkId in destNetworkIds)
            {
                var network = networks.FirstOrDefault(n =>
                    string.Equals(n.Id, networkId, StringComparison.OrdinalIgnoreCase));
                if (network != null)
                {
                    purposes.Add(network.Purpose);
                }
            }
        }
        // Second try: Check DestinationIps - find which network's subnet they belong to
        else if (rule.DestinationIps != null && rule.DestinationIps.Count > 0)
        {
            foreach (var destIp in rule.DestinationIps)
            {
                // Skip IP ranges for now, just check single IPs
                var ip = destIp.Contains('-') ? destIp.Split('-')[0] : destIp;
                // Skip CIDR notation, just check single IPs
                if (ip.Contains('/'))
                    ip = ip.Split('/')[0];

                // Find which network this IP belongs to
                foreach (var network in networks)
                {
                    if (!string.IsNullOrEmpty(network.Subnet) &&
                        FirewallRuleOverlapDetector.IpMatchesCidr(ip, network.Subnet))
                    {
                        purposes.Add(network.Purpose);
                        break; // Found the network for this IP
                    }
                }
            }
        }

        // If all destinations have the same purpose, include it in the description
        if (purposes.Count == 1)
        {
            var purpose = purposes.First();
            return purpose switch
            {
                NetworkPurpose.IoT => " (IoT)",
                NetworkPurpose.Security => " (Security)",
                NetworkPurpose.Management => " (Management)",
                NetworkPurpose.Guest => " (Guest)",
                NetworkPurpose.Corporate => " (Corporate)",
                NetworkPurpose.Home => " (Home)",
                _ => ""
            };
        }

        // Multiple different purposes or unknown - no suffix
        return "";
    }

    /// <summary>
    /// Check if an allow rule is for a known management service (UniFi, AFC, NTP, 5G).
    /// These exceptions are already covered by MGMT_MISSING_* audit rules and don't need
    /// to be reported as generic firewall exceptions.
    /// </summary>
    private static bool IsKnownManagementServiceException(FirewallRule allowRule)
    {
        // Check web domains for known management service domains
        if (allowRule.WebDomains != null)
        {
            foreach (var domain in allowRule.WebDomains)
            {
                // UniFi cloud management
                if (domain.Contains("ui.com", StringComparison.OrdinalIgnoreCase))
                    return true;

                // AFC (Automated Frequency Coordination) for 6GHz WiFi
                if (domain.Contains("qcs.qualcomm.com", StringComparison.OrdinalIgnoreCase))
                    return true;

                // NTP time sync (domain-based)
                if (domain.Contains("ntp.org", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 5G/LTE modem registration (use helper for consistency)
            if (Allows5GRegistrationDomains(allowRule))
                return true;
        }

        // NTP port-based rule (UDP 123)
        if (FirewallGroupHelper.RuleAllowsPortAndProtocol(allowRule, "123", "udp"))
            return true;

        return false;
    }
}

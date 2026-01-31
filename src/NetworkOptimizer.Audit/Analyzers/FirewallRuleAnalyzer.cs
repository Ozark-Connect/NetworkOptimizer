using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;
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
                                Severity = AuditSeverity.Recommended,
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

                var directionDesc = isAnySource ? "from any source" : "to any destination";
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.BroadRule,
                    Severity = AuditSeverity.Recommended,
                    Message = $"Broad rule '{rule.Name}' allows traffic {directionDesc}",
                    Metadata = new Dictionary<string, object>
                    {
                        { "rule_name", rule.Name ?? rule.Id },
                        { "rule_index", rule.Index },
                        { "ruleset", rule.Ruleset ?? "default" },
                        { "direction", directionDesc }
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
    ///
    /// IMPORTANT: UniFi's "Network Isolation" feature only blocks OUTBOUND traffic FROM the isolated network.
    /// It does NOT block INBOUND traffic TO the isolated network. Therefore:
    /// - For SOURCE networks (IoT, Guest): we filter by !NetworkIsolationEnabled because if they have
    ///   isolation enabled, they can't initiate outbound connections anyway.
    /// - For DESTINATION networks (Management, Security): we must check ALL networks regardless of their
    ///   isolation status, because isolation doesn't protect them from inbound access.
    ///
    /// UniFi Guest networks (purpose="guest") have implicit isolation at switch/AP level, so skip them.
    /// </summary>
    /// <param name="rules">Firewall rules to analyze</param>
    /// <param name="networks">Network configurations</param>
    /// <param name="externalZoneId">External zone ID - rules targeting this zone are not inter-VLAN rules</param>
    public List<AuditIssue> CheckInterVlanIsolation(List<FirewallRule> rules, List<NetworkInfo> networks, string? externalZoneId = null)
    {
        var issues = new List<AuditIssue>();

        // ============================================================================
        // DESTINATION NETWORKS (what we're protecting)
        // Do NOT filter by isolation status - UniFi isolation only blocks outbound,
        // not inbound. We need explicit block rules to protect these networks.
        // ============================================================================
        var allSecurityNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Security).ToList();
        var allManagementNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Management).ToList();

        // ============================================================================
        // SOURCE NETWORKS (what's trying to reach protected networks)
        // Filter by !NetworkIsolationEnabled because if source has isolation enabled,
        // it can't initiate outbound connections anyway (isolation blocks outbound).
        // UniFi Guest networks have implicit isolation at switch/AP level, so skip them too.
        // ============================================================================

        // SIMPLIFIED: Everything (except Security) should be blocked from reaching Security
        // This covers: Corporate, Home, IoT, Guest, Management, Printer, DMZ, Unknown → Security
        var networksToBlockFromSecurity = networks
            .Where(n => n.Purpose != NetworkPurpose.Security && !n.NetworkIsolationEnabled)
            .Where(n => n.Purpose != NetworkPurpose.Guest || !n.IsUniFiGuestNetwork) // Skip UniFi guest networks
            .ToList();

        foreach (var srcNet in networksToBlockFromSecurity)
        {
            foreach (var security in allSecurityNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, srcNet, security, "FW-ISOLATION-SEC");
            }
        }

        // SIMPLIFIED: Everything (except Management) should be blocked from reaching Management
        // This covers: Corporate, Home, IoT, Guest, Security, Printer, DMZ, Unknown → Management
        var networksToBlockFromManagement = networks
            .Where(n => n.Purpose != NetworkPurpose.Management && !n.NetworkIsolationEnabled)
            .Where(n => n.Purpose != NetworkPurpose.Guest || !n.IsUniFiGuestNetwork) // Skip UniFi guest networks
            .ToList();

        foreach (var srcNet in networksToBlockFromManagement)
        {
            foreach (var mgmt in allManagementNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, srcNet, mgmt, "FW-ISOLATION-MGMT");
            }
        }

        // ============================================================================
        // ADDITIONAL ISOLATION CHECKS (separate concerns)
        // These are about isolating untrusted networks from trusted networks
        // ============================================================================

        // Trusted networks that IoT/Guest should not access
        var corporateNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Corporate).ToList();
        var homeNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Home).ToList();
        var trustedNetworks = corporateNetworks.Concat(homeNetworks).ToList();

        // IoT should be isolated from: Corporate, Home
        // (IoT → Security and IoT → Management already covered above)
        var iotNetworks = networks.Where(n => n.Purpose == NetworkPurpose.IoT && !n.NetworkIsolationEnabled).ToList();
        foreach (var iot in iotNetworks)
        {
            foreach (var trusted in trustedNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, iot, trusted, "FW-ISOLATION-IOT");
            }
        }

        // Guest should be isolated from: Corporate, Home, IoT
        // (Guest → Security and Guest → Management already covered above)
        var guestNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Guest && !n.NetworkIsolationEnabled && !n.IsUniFiGuestNetwork).ToList();
        var allIotNetworks = networks.Where(n => n.Purpose == NetworkPurpose.IoT).ToList();

        foreach (var guest in guestNetworks)
        {
            foreach (var trusted in trustedNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, guest, trusted, "FW-ISOLATION-GUEST");
            }
            // Guest should be isolated from IoT (guests shouldn't control smart home devices)
            foreach (var iot in allIotNetworks)
            {
                CheckAndAddIsolationIssue(issues, rules, guest, iot, "FW-ISOLATION-GUEST-IOT");
            }
        }

        // ============================================================================
        // CHECK FOR ALLOW RULES BETWEEN NETWORKS THAT SHOULD BE ISOLATED
        // This catches rules that explicitly open up traffic between isolated network types
        // ============================================================================
        var allGuestNetworks = networks.Where(n => n.Purpose == NetworkPurpose.Guest).ToList();
        var trustedPlusManagement = trustedNetworks.Concat(allManagementNetworks).ToList();

        // Check for allow rules from any network to Security
        foreach (var srcNet in networksToBlockFromSecurity)
        {
            foreach (var security in allSecurityNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, srcNet, security, externalZoneId);
            }
        }

        // Check for allow rules from any network to Management
        foreach (var srcNet in networksToBlockFromManagement)
        {
            foreach (var mgmt in allManagementNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, srcNet, mgmt, externalZoneId);
            }
        }

        // Check for allow rules between IoT and trusted networks
        foreach (var iot in allIotNetworks)
        {
            foreach (var trusted in trustedPlusManagement)
            {
                CheckForProblematicAllowRules(issues, rules, iot, trusted, externalZoneId);
            }
        }

        // Check for allow rules between Guest and trusted/IoT networks
        foreach (var guest in allGuestNetworks)
        {
            foreach (var trusted in trustedPlusManagement)
            {
                CheckForProblematicAllowRules(issues, rules, guest, trusted, externalZoneId);
            }
            foreach (var iot in allIotNetworks)
            {
                CheckForProblematicAllowRules(issues, rules, guest, iot, externalZoneId);
            }
        }

        return issues;
    }

    /// <summary>
    /// Detect user-created ALLOW rules that create exceptions to the UniFi-managed "Isolated Networks" rules.
    /// When a network has NetworkIsolationEnabled, UniFi creates predefined BLOCK rules that block traffic
    /// FROM isolated networks to other destinations. User ALLOW rules that allow traffic FROM these
    /// isolated networks create exceptions that should be reported as Info issues.
    /// Note: Traffic TO isolated networks is not blocked by the predefined rules, so we only check source.
    /// </summary>
    /// <param name="rules">Firewall rules to analyze (including predefined rules)</param>
    /// <param name="networks">Network configurations</param>
    /// <returns>List of Info-level issues for network isolation exceptions</returns>
    /// <summary>
    /// Detect user-created rules that create exceptions to the predefined "Isolated Networks" rules.
    /// Only flags rules allowing traffic FROM isolated networks TO other internal networks (inter-VLAN).
    /// Rules allowing traffic to external/internet are NOT flagged as they don't violate isolation.
    /// </summary>
    /// <param name="rules">Firewall rules to analyze</param>
    /// <param name="networks">Network configurations</param>
    /// <param name="externalZoneId">External zone ID - rules targeting this zone are internet access, not isolation exceptions</param>
    public List<AuditIssue> DetectNetworkIsolationExceptions(List<FirewallRule> rules, List<NetworkInfo> networks, string? externalZoneId = null)
    {
        var issues = new List<AuditIssue>();

        // Find networks that have isolation enabled (these have predefined "Isolated Networks" rules)
        var isolatedNetworks = networks.Where(n => n.NetworkIsolationEnabled).ToList();

        if (!isolatedNetworks.Any())
        {
            _logger.LogDebug("No networks with isolation enabled found");
            return issues;
        }

        // Verify there are predefined "Isolated Networks" rules
        var isolatedNetworkRules = rules.Where(r =>
            r.Predefined &&
            r.Enabled &&
            r.ActionType.IsBlockAction() &&
            string.Equals(r.Name, "Isolated Networks", StringComparison.OrdinalIgnoreCase)).ToList();

        if (!isolatedNetworkRules.Any())
        {
            _logger.LogDebug("No predefined 'Isolated Networks' rules found");
            return issues;
        }

        _logger.LogDebug("Found {Count} networks with isolation enabled and {RuleCount} 'Isolated Networks' rules",
            isolatedNetworks.Count, isolatedNetworkRules.Count);

        // Find user-created ALLOW rules that allow traffic FROM isolated networks
        // The predefined "Isolated Networks" rules block traffic FROM isolated networks TO other VLANs,
        // so only ALLOW rules with isolated networks as SOURCE that target INTERNAL networks are exceptions.
        // Rules targeting the external zone (internet) are NOT isolation exceptions.
        var userAllowRules = rules.Where(r =>
            !r.Predefined &&
            r.Enabled &&
            r.ActionType.IsAllowAction() &&
            !IsExternalZoneRule(r, externalZoneId)).ToList(); // Exclude internet-bound rules

        foreach (var rule in userAllowRules)
        {
            // Check if this rule allows traffic FROM an isolated network (source only)
            // Traffic TO isolated networks is implicitly allowed, so we don't check destination
            var sourceIsolatedNetworks = GetInvolvedIsolatedNetworks(rule, isolatedNetworks, isSource: true);

            if (sourceIsolatedNetworks.Any())
            {
                // Skip required management access rules (NTP, UniFi, AFC, 5G) - these are expected
                var mgmtNetworks = sourceIsolatedNetworks.Where(n => n.Purpose == NetworkPurpose.Management).ToList();
                if (mgmtNetworks.Any() && IsRequiredManagementAccessRule(rule))
                {
                    _logger.LogDebug("Skipping required management access rule '{RuleName}'", rule.Name);
                    continue;
                }

                // Use "Source -> Destination" format for consistent grouping with AllowExceptionPattern
                var description = GetSourceToDestinationDescription(rule, networks);

                var networkNames = string.Join(", ", sourceIsolatedNetworks.Select(n => n.Name));

                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.NetworkIsolationException,
                    Severity = AuditSeverity.Informational,
                    Message = $"Allow rule '{rule.Name}' creates an exception to network isolation for: {networkNames}",
                    Description = description,
                    Metadata = new Dictionary<string, object>
                    {
                        { "rule_name", rule.Name ?? rule.Id },
                        { "rule_index", rule.Index },
                        { "isolated_networks", networkNames },
                        { "pattern", "isolation_exception" }
                    },
                    RuleId = "FW-ISOLATION-EXCEPTION-001",
                    ScoreImpact = 0,
                    RecommendedAction = "This appears to be a deliberate exception pattern - no action required"
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Get the isolated networks involved in a firewall rule as source.
    /// Checks both network ID references AND IP/CIDR-based sources that cover the network's subnet.
    /// </summary>
    private List<NetworkInfo> GetInvolvedIsolatedNetworks(FirewallRule rule, List<NetworkInfo> isolatedNetworks, bool isSource)
    {
        var result = new List<NetworkInfo>();

        foreach (var network in isolatedNetworks)
        {
            bool isInvolved = false;

            if (isSource)
            {
                // First check network ID reference
                isInvolved = AppliesToSourceNetwork(rule, network.Id);

                // Also check if rule source is IP/CIDR that covers the network's subnet
                if (!isInvolved && !string.IsNullOrEmpty(network.Subnet))
                {
                    isInvolved = SourceCidrsCoversNetworkSubnet(rule, network.Subnet);
                }
            }
            else
            {
                isInvolved = AppliesToDestinationNetwork(rule, network.Id);
            }

            if (isInvolved)
            {
                result.Add(network);
            }
        }

        return result;
    }

    /// <summary>
    /// Check if a rule's source IP/CIDRs cover a network's subnet.
    /// This catches rules that use IP-based source matching instead of network references.
    /// </summary>
    private static bool SourceCidrsCoversNetworkSubnet(FirewallRule rule, string networkSubnet)
    {
        // Check if source matching type is IP-based
        if (string.IsNullOrEmpty(rule.SourceMatchingTarget) ||
            !rule.SourceMatchingTarget.Equals("IP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NetworkUtilities.AnyCidrCoversSubnet(rule.SourceIps, networkSubnet);
    }

    /// <summary>
    /// Get a purpose suffix for isolation exception grouping based on the network purposes involved.
    /// </summary>
    private static string GetIsolationExceptionPurposeSuffix(List<NetworkInfo> sourceNetworks)
    {
        // Collect unique purposes from source networks
        var purposes = sourceNetworks
            .Select(n => n.Purpose)
            .Distinct()
            .ToList();

        if (purposes.Count == 1)
        {
            return purposes[0] switch
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

        // Multiple purposes - check for common patterns
        if (purposes.Count == 2)
        {
            // Sort for consistent naming
            var sorted = purposes.OrderBy(p => p.ToString()).ToList();

            // Management exceptions are common
            if (sorted.Contains(NetworkPurpose.Management))
            {
                return " (Management)";
            }

            // Security/IoT exceptions
            if (sorted.Contains(NetworkPurpose.Security) && sorted.Contains(NetworkPurpose.IoT))
            {
                return " (Security/IoT)";
            }
        }

        return "";
    }

    /// <summary>
    /// Check if a rule is a required management access rule (NTP, UniFi, AFC, 5G).
    /// These are expected rules for isolated management networks and should not be flagged.
    /// </summary>
    private static bool IsRequiredManagementAccessRule(FirewallRule rule)
    {
        // Check for UniFi cloud access (ui.com)
        if (rule.WebDomains?.Any(d => d.Contains("ui.com", StringComparison.OrdinalIgnoreCase)) == true)
            return true;

        // Check for AFC access (qcs.qualcomm.com)
        if (rule.WebDomains?.Any(d => d.Contains("qcs.qualcomm.com", StringComparison.OrdinalIgnoreCase)) == true)
            return true;

        // Check for NTP access (UDP port 123)
        if (FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "123", "udp"))
            return true;

        // Check for 5G/LTE carrier domains
        var carrierDomains = new[] { "trafficmanager.net", "t-mobile.com", "gsma.com" };
        if (rule.WebDomains?.Any(d => carrierDomains.Any(cd => d.Contains(cd, StringComparison.OrdinalIgnoreCase))) == true)
            return true;

        return false;
    }

    /// <summary>
    /// Helper to find and flag ALLOW rules between networks that should be isolated.
    /// Rules targeting the External zone are skipped - they're for outbound internet access, not inter-VLAN traffic.
    /// Uses FirewallRuleEvaluator to account for rule ordering - only flags allow rules that actually take effect.
    ///
    /// Note: This is unidirectional - checks sourceNetwork → destNetwork only.
    /// The caller must specify the correct direction based on the isolation requirement:
    /// - For isolated networks (IoT, Guest): Check isolated → other (outbound from isolated)
    /// - For protected networks (Management, Security): Check other → protected (inbound to protected)
    /// </summary>
    private void CheckForProblematicAllowRules(
        List<AuditIssue> issues,
        List<FirewallRule> rules,
        NetworkInfo sourceNetwork,
        NetworkInfo destNetwork,
        string? externalZoneId)
    {
        // Don't check network against itself
        if (sourceNetwork.Id == destNetwork.Id)
            return;

        // Filter to non-predefined, non-external-zone rules for evaluation
        var relevantRules = rules.Where(r =>
            !r.Predefined &&
            !IsExternalZoneRule(r, externalZoneId))
            .ToList();

        // Check only the specified direction (source → dest)
        CheckDirectionForProblematicAllowRule(issues, relevantRules, sourceNetwork, destNetwork);
    }

    /// <summary>
    /// Check a single direction (source → dest) for problematic allow rules.
    /// </summary>
    private void CheckDirectionForProblematicAllowRule(
        List<AuditIssue> issues,
        List<FirewallRule> rules,
        NetworkInfo sourceNet,
        NetworkInfo destNet)
    {
        // Use FirewallRuleEvaluator to find the effective rule for this traffic direction
        var evalResult = FirewallRuleEvaluator.Evaluate(rules,
            r => HasNetworkPair(r, sourceNet, destNet));

        // Only flag if traffic is effectively allowed (allow rule takes effect)
        if (!evalResult.IsAllowed)
            return;

        var effectiveRule = evalResult.EffectiveRule!;

        issues.Add(new AuditIssue
        {
            Type = IssueTypes.IsolationBypassed,
            Severity = AuditSeverity.Critical,
            Message = $"Rule '{effectiveRule.Name}' allows traffic from {sourceNet.Name} ({sourceNet.Purpose}) to {destNet.Name} ({destNet.Purpose}) which should be isolated",
            Metadata = new Dictionary<string, object>
            {
                { "rule_name", effectiveRule.Name ?? effectiveRule.Id },
                { "rule_index", effectiveRule.Index },
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

    /// <summary>
    /// Helper to check for isolation rule between two networks and add issue if missing.
    /// Checks if sourceNetwork can reach destNetwork.
    ///
    /// Protection can come from:
    /// 1. Source network has isolation enabled (blocks all outbound from source)
    /// 2. A firewall rule specifically blocking sourceNetwork → destNetwork
    ///
    /// Note: Destination's isolation status is irrelevant - it only blocks the destination's outbound,
    /// not incoming traffic from other networks.
    /// </summary>
    private void CheckAndAddIsolationIssue(
        List<AuditIssue> issues,
        List<FirewallRule> rules,
        NetworkInfo sourceNetwork,
        NetworkInfo destNetwork,
        string ruleIdPrefix)
    {
        // Don't check network against itself
        if (sourceNetwork.Id == destNetwork.Id)
            return;

        // If source network has isolation enabled, it can't reach the destination (or anywhere)
        if (sourceNetwork.NetworkIsolationEnabled)
            return;

        // Evaluate firewall rules considering rule ordering (lower index = higher priority)
        var evalResult = FirewallRuleEvaluator.Evaluate(rules,
            r => HasNetworkPair(r, sourceNetwork, destNetwork));

        // For isolation, the rule must:
        // 1. Be a block action (checked by IsBlocked)
        // 2. Block NEW connections (checked by IsBlocked via BlocksNewConnections)
        // 3. Block ALL traffic, not just specific ports/protocols/domains
        var hasIsolationRule = evalResult.IsBlocked && BlocksAllTraffic(evalResult.EffectiveRule!);

        if (evalResult.BlockRuleEclipsed)
        {
            _logger.LogDebug("Isolation {Source} → {Dest}: Allow rule '{AllowRule}' (index={AllowIndex}) eclipses block rule '{BlockRule}' (index={BlockIndex})",
                sourceNetwork.Name, destNetwork.Name,
                evalResult.EffectiveRule?.Name, evalResult.EffectiveRule?.Index,
                evalResult.EclipsedBlockRule?.Name, evalResult.EclipsedBlockRule?.Index);
        }

        if (hasIsolationRule)
        {
            _logger.LogDebug("Isolation {Source} → {Dest} satisfied by rule '{RuleName}' (src={SrcTarget}, dst={DstTarget}, index={Index})",
                sourceNetwork.Name, destNetwork.Name, evalResult.EffectiveRule!.Name,
                evalResult.EffectiveRule.SourceMatchingTarget, evalResult.EffectiveRule.DestinationMatchingTarget, evalResult.EffectiveRule.Index);
        }

        if (!hasIsolationRule)
        {
            // If there's a non-predefined effective allow rule, CheckForProblematicAllowRules will catch it
            // with a more specific "Isolation Bypassed" message - skip the generic "Missing Isolation".
            // But if the allow rule is predefined (like "Allow All Traffic"), we must report "Missing Isolation"
            // because CheckForProblematicAllowRules filters out predefined rules.
            if (evalResult.IsAllowed && evalResult.EffectiveRule?.Predefined != true)
            {
                _logger.LogDebug("Isolation {Source} → {Dest}: Skipping 'Missing Isolation' - non-predefined allow rule '{RuleName}' will be reported as 'Isolation Bypassed'",
                    sourceNetwork.Name, destNetwork.Name, evalResult.EffectiveRule?.Name);
                return;
            }

            // Determine severity based on network types
            // Critical: Guest to sensitive networks, anything to Management
            var isCritical = IsCriticalIsolationMissing(sourceNetwork.Purpose, destNetwork.Purpose);
            var severity = isCritical ? AuditSeverity.Critical : AuditSeverity.Recommended;
            var scoreImpact = isCritical ? 12 : 7;

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.MissingIsolation,
                Severity = severity,
                Message = $"No rule blocking {sourceNetwork.Name} ({sourceNetwork.Purpose}) from reaching {destNetwork.Name} ({destNetwork.Purpose})",
                Metadata = new Dictionary<string, object>
                {
                    { "source_network", sourceNetwork.Name },
                    { "source_purpose", sourceNetwork.Purpose.ToString() },
                    { "dest_network", destNetwork.Name },
                    { "dest_purpose", destNetwork.Purpose.ToString() },
                    { "recommendation", $"Add block rule: {sourceNetwork.Name} → {destNetwork.Name}. Network Isolation is outbound-only and easily bypassed." }
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
            // Also filter out allow rules that are eclipsed by block rules with lower index
            var broadAllowRules = rules.Where(rule =>
                rule.Enabled &&
                !rule.Predefined &&
                rule.ActionType.IsAllowAction() &&
                RuleAppliesToNetwork(rule, network) &&
                IsBroadExternalAccess(rule, externalZoneId, broadInternetAppIds) &&
                // Only report if the allow rule actually takes effect (not eclipsed by a block rule)
                !IsAllowRuleEclipsedByBlockRule(rules, rule, network.Id, externalZoneId)).ToList();

            foreach (var rule in broadAllowRules)
            {
                var accessType = GetBroadAccessDescription(rule, externalZoneId);
                issues.Add(new AuditIssue
                {
                    Type = IssueTypes.InternetBlockBypassed,
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
    /// Also checks if IP-based sources cover the network's subnet.
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

        // IP source - check if CIDRs cover the network's subnet
        if (sourceTarget == "IP" && !string.IsNullOrEmpty(network.Subnet))
        {
            return SourceCidrsCoversNetworkSubnet(rule, network.Subnet);
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
        issues.AddRange(DetectNetworkIsolationExceptions(rules, networks, externalZoneId));

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
            _logger.LogDebug("Checking firewall access for isolated management network '{Name}' (ID: {Id})", mgmtNetwork.Name, mgmtNetwork.Id);

            // Check for UniFi cloud access rule (config-based only)
            // Must have: source = management network, destination web domain = ui.com, TCP allowed
            // Also check if the allow rule is eclipsed by a block rule with lower index

            // Debug: log all rules with ui.com domain
            foreach (var r in rules.Where(r => r.WebDomains?.Any(d => d.Contains("ui.com", StringComparison.OrdinalIgnoreCase)) == true))
            {
                var appliesToSource = AppliesToSourceNetwork(r, mgmtNetwork.Id);
                var allowsTcp = FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp");
                _logger.LogDebug("UniFi rule candidate '{Name}': enabled={Enabled}, isAllow={IsAllow}, appliesToMgmt={AppliesTo}, allowsTcp={AllowsTcp}, sourceTarget={SourceTarget}, sourceNets={SourceNets}",
                    r.Name, r.Enabled, r.ActionType.IsAllowAction(), appliesToSource, allowsTcp,
                    r.SourceMatchingTarget, string.Join(",", r.SourceNetworkIds ?? new List<string>()));
            }

            var unifiAllowRule = rules.FirstOrDefault(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                r.WebDomains?.Any(d => d.Contains("ui.com", StringComparison.OrdinalIgnoreCase)) == true &&
                FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp"));

            var hasUniFiAccess = unifiAllowRule != null && !IsAllowRuleEclipsedByBlockRule(rules, unifiAllowRule, mgmtNetwork.Id, externalZoneId);
            _logger.LogDebug("UniFi access check: foundRule={FoundRule}, hasAccess={HasAccess}", unifiAllowRule?.Name, hasUniFiAccess);

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
                    RecommendedAction = "Add firewall rule allowing TCP 443 to ui.com for UniFi cloud management. If rule exists, ensure it isn't overridden by a block rule higher in the rule order."
                });
            }

            // Check for AFC (Automated Frequency Coordination) traffic rule - needed for 6GHz WiFi
            // Must have: source = management network, destination web domain = qcs.qualcomm.com, TCP allowed
            // Also check if the allow rule is eclipsed by a block rule with lower index
            var afcAllowRule = rules.FirstOrDefault(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                r.WebDomains?.Any(d => d.Contains("qcs.qualcomm.com", StringComparison.OrdinalIgnoreCase)) == true &&
                FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp"));

            var hasAfcAccess = afcAllowRule != null && !IsAllowRuleEclipsedByBlockRule(rules, afcAllowRule, mgmtNetwork.Id, externalZoneId);

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
                    RecommendedAction = "Add firewall rule allowing AFC traffic for 6GHz WiFi coordination. If rule exists, ensure it isn't overridden by a block rule higher in the rule order."
                });
            }

            // Check for NTP access rule - needed for time sync (required for AFC)
            // NTP uses UDP port 123 - domain filtering doesn't help since NTP talks directly to IP addresses
            // Also check if the allow rule is eclipsed by a block rule with lower index
            var ntpAllowRule = rules.FirstOrDefault(r =>
                r.Enabled &&
                r.ActionType.IsAllowAction() &&
                AppliesToSourceNetwork(r, mgmtNetwork.Id) &&
                FirewallGroupHelper.RuleAllowsPortAndProtocol(r, "123", "udp") &&
                TargetsExternalZone(r, externalZoneId));

            var hasNtpAccess = ntpAllowRule != null &&
                !IsNonWebAllowRuleEclipsed(rules, ntpAllowRule, mgmtNetwork.Id, externalZoneId, "123", "udp");

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
                    RecommendedAction = "Add firewall rule allowing NTP traffic (UDP port 123 to External zone). If rule exists, ensure it isn't overridden by a block rule higher in the rule order."
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
            // Also check if the allow rule is eclipsed by a block rule with lower index
            if (has5GDevice)
            {
                var modem5GAllowRule = rules.FirstOrDefault(r =>
                    r.Enabled &&
                    r.ActionType.IsAllowAction() &&
                    Allows5GRegistrationDomains(r) &&
                    FirewallGroupHelper.AllowsProtocol(r.Protocol, r.MatchOppositeProtocol, "tcp") &&
                    // Source can be: management network, specific IP, specific MAC, or ANY
                    (AppliesToSourceNetwork(r, mgmtNetwork.Id) ||
                     IsSourceIpBased(r) ||
                     IsSourceMacBased(r) ||
                     IsAnySource(r)));

                var has5GModemAccess = modem5GAllowRule != null &&
                    !Is5GModemAllowRuleEclipsed(rules, modem5GAllowRule, mgmtNetwork, externalZoneId);

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
                        RecommendedAction = "Add firewall rule allowing 5G/LTE modem registration traffic (trafficmanager.net, t-mobile.com, gsma.com). If rule exists, ensure it isn't overridden by a block rule higher in the rule order."
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
    /// Checks if a block rule affects the same source as an allow rule.
    /// Used to determine if a block rule would eclipse an allow rule.
    /// This method examines the allow rule's actual source specification rather than
    /// requiring a network ID to be passed in.
    /// </summary>
    /// <param name="blockRule">The block rule to check</param>
    /// <param name="allowRule">The allow rule whose source we're checking against</param>
    /// <returns>True if the block rule affects the same source as the allow rule</returns>
    private static bool BlockRuleAffectsSameSource(FirewallRule blockRule, FirewallRule allowRule)
    {
        // Block rule with ANY source affects all sources
        if (IsAnySource(blockRule))
            return true;

        // Check based on allow rule's source type
        var allowSourceTarget = allowRule.SourceMatchingTarget?.ToUpperInvariant() ?? "";

        switch (allowSourceTarget)
        {
            case "ANY":
                // Allow rule matches everything - any block rule source is a subset
                return true;

            case "NETWORK":
                // Allow rule targets specific networks
                if (blockRule.SourceMatchingTarget?.Equals("NETWORK", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var allowNetworks = allowRule.SourceNetworkIds ?? new List<string>();
                    if (allowNetworks.Count == 0) return false;

                    // Check if any network in the allow rule is affected by the block rule
                    return allowNetworks.Any(netId => AppliesToSourceNetwork(blockRule, netId));
                }
                return false;

            case "IP":
                // Allow rule targets specific IPs
                if (blockRule.SourceMatchingTarget?.Equals("IP", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var allowIps = allowRule.SourceIps ?? new List<string>();
                    var blockIps = blockRule.SourceIps ?? new List<string>();
                    // Check if any IP overlaps
                    return allowIps.Any(ip => blockIps.Contains(ip));
                }
                // Network-based block rules could theoretically contain the IPs,
                // but that requires subnet matching which is complex - be conservative
                return false;

            case "CLIENT":
                // Allow rule targets specific MACs
                if (blockRule.SourceMatchingTarget?.Equals("CLIENT", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var allowMacs = allowRule.SourceClientMacs ?? new List<string>();
                    var blockMacs = blockRule.SourceClientMacs ?? new List<string>();
                    return allowMacs.Any(mac =>
                        blockMacs.Contains(mac, StringComparer.OrdinalIgnoreCase));
                }
                return false;

            default:
                // Unknown source type or legacy format - fall back to checking if block uses ANY
                return false;
        }
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
    /// Check if a firewall rule applies to traffic from a specific source network.
    /// Also checks if IP-based sources cover the network's subnet.
    /// </summary>
    /// <param name="rule">The firewall rule to check</param>
    /// <param name="network">The network to check against</param>
    /// <returns>True if the rule applies to traffic from the specified network</returns>
    private static bool AppliesToSourceNetwork(FirewallRule rule, NetworkInfo network)
    {
        // First check network ID
        if (AppliesToSourceNetwork(rule, network.Id))
            return true;

        // Also check if IP-based source covers the network's subnet
        if (!string.IsNullOrEmpty(network.Subnet) &&
            rule.SourceMatchingTarget?.Equals("IP", StringComparison.OrdinalIgnoreCase) == true)
        {
            return SourceCidrsCoversNetworkSubnet(rule, network.Subnet);
        }

        return false;
    }

    /// <summary>
    /// Check if a firewall rule applies to traffic to a specific destination network.
    /// Also checks if IP-based destinations cover the network's subnet.
    /// </summary>
    /// <param name="rule">The firewall rule to check</param>
    /// <param name="network">The network to check against</param>
    /// <returns>True if the rule applies to traffic to the specified network</returns>
    private static bool AppliesToDestinationNetwork(FirewallRule rule, NetworkInfo network)
    {
        // First check network ID
        if (AppliesToDestinationNetwork(rule, network.Id))
            return true;

        // Also check if IP-based destination covers the network's subnet
        if (!string.IsNullOrEmpty(network.Subnet) &&
            rule.DestinationMatchingTarget?.Equals("IP", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DestinationCidrsCoversNetworkSubnet(rule, network.Subnet);
        }

        return false;
    }

    /// <summary>
    /// Check if a rule's destination IP/CIDRs cover a network's subnet.
    /// </summary>
    private static bool DestinationCidrsCoversNetworkSubnet(FirewallRule rule, string networkSubnet)
    {
        return NetworkUtilities.AnyCidrCoversSubnet(rule.DestinationIps, networkSubnet);
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
    /// Check if a firewall rule matches a specific source->destination network pair.
    /// Also checks if IP-based source/destination CIDRs cover the network's subnet.
    /// Includes zone matching - rule's zones must match the networks' zones.
    /// </summary>
    private static bool HasNetworkPair(FirewallRule rule, NetworkInfo sourceNetwork, NetworkInfo destNetwork)
    {
        // Check zone matching first - rule's zones must match the networks' zones
        // If the rule has a source zone, it must match the source network's zone
        if (!string.IsNullOrEmpty(rule.SourceZoneId) && !string.IsNullOrEmpty(sourceNetwork.FirewallZoneId))
        {
            if (!string.Equals(rule.SourceZoneId, sourceNetwork.FirewallZoneId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // If the rule has a destination zone, it must match the destination network's zone
        if (!string.IsNullOrEmpty(rule.DestinationZoneId) && !string.IsNullOrEmpty(destNetwork.FirewallZoneId))
        {
            if (!string.Equals(rule.DestinationZoneId, destNetwork.FirewallZoneId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return AppliesToSourceNetwork(rule, sourceNetwork) && AppliesToDestinationNetwork(rule, destNetwork);
    }

    /// <summary>
    /// Check if a firewall rule blocks ALL traffic (no port/protocol/domain restrictions).
    /// A rule that only blocks specific ports, protocols, or web domains doesn't provide
    /// full network isolation - traffic on other ports/protocols can still pass.
    /// </summary>
    private static bool BlocksAllTraffic(FirewallRule rule)
    {
        // Must be a block action
        if (!rule.ActionType.IsBlockAction())
            return false;

        // Protocol must be "all" or empty (meaning all protocols)
        if (!string.IsNullOrEmpty(rule.Protocol) &&
            !rule.Protocol.Equals("all", StringComparison.OrdinalIgnoreCase))
            return false;

        // No port restrictions
        if (!string.IsNullOrEmpty(rule.SourcePort) || !string.IsNullOrEmpty(rule.DestinationPort))
            return false;

        // No web domain restrictions (rules targeting specific domains don't block all traffic)
        if (rule.WebDomains != null && rule.WebDomains.Count > 0)
            return false;

        // No app category restrictions
        if (rule.AppCategoryIds != null && rule.AppCategoryIds.Count > 0)
            return false;

        return true;
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
    /// Checks if a specific allow rule is eclipsed by a block rule with lower index.
    /// A block rule eclipses the allow rule if it:
    /// 1. Has a lower index (higher priority)
    /// 2. Applies to the same source network
    /// 3. Would actually block the same traffic (not just narrower/different traffic)
    /// </summary>
    private bool IsAllowRuleEclipsedByBlockRule(
        List<FirewallRule> rules,
        FirewallRule allowRule,
        string sourceNetworkId,
        string? externalZoneId)
    {
        var eclipsingRule = rules.FirstOrDefault(r =>
            r.Enabled &&
            r.ActionType.IsBlockAction() &&
            r.Index < allowRule.Index &&
            AppliesToSourceNetwork(r, sourceNetworkId) &&
            // Block rule must affect the same traffic
            WouldBlockSameTraffic(r, allowRule, externalZoneId));

        if (eclipsingRule != null)
        {
            _logger.LogDebug("Allow rule '{AllowRule}' (index {AllowIndex}) eclipsed by block rule '{BlockRule}' (index {BlockIndex}, destTarget={DestTarget})",
                allowRule.Name, allowRule.Index, eclipsingRule.Name, eclipsingRule.Index,
                eclipsingRule.DestinationMatchingTarget);
        }

        return eclipsingRule != null;
    }

    /// <summary>
    /// Checks if a non-WEB allow rule (port/protocol based) is eclipsed by a block rule.
    /// Used for NTP access, etc. where we need to check specific port/protocol.
    /// </summary>
    private bool IsNonWebAllowRuleEclipsed(
        List<FirewallRule> rules,
        FirewallRule allowRule,
        string sourceNetworkId,
        string? externalZoneId,
        string port,
        string protocol)
    {
        var eclipsingRule = rules.FirstOrDefault(r =>
            r.Enabled &&
            r.ActionType.IsBlockAction() &&
            r.Index < allowRule.Index &&
            // WEB-based block rules target specific web domains, not arbitrary port/protocol traffic
            !string.Equals(r.DestinationMatchingTarget, "WEB", StringComparison.OrdinalIgnoreCase) &&
            AppliesToSourceNetwork(r, sourceNetworkId) &&
            TargetsExternalZone(r, externalZoneId) &&
            FirewallGroupHelper.RuleBlocksPortAndProtocol(r, port, protocol));

        if (eclipsingRule != null)
        {
            _logger.LogDebug("Non-WEB allow rule '{AllowRule}' (index {AllowIndex}) eclipsed by block rule '{BlockRule}' (index {BlockIndex}) for port {Port}/{Protocol}",
                allowRule.Name, allowRule.Index, eclipsingRule.Name, eclipsingRule.Index, port, protocol);
        }

        return eclipsingRule != null;
    }

    /// <summary>
    /// Checks if a 5G modem WEB-based allow rule is eclipsed by a block rule.
    /// The 5G modem allow rule can have different source types (NETWORK, IP, MAC, ANY),
    /// so we need to check based on the actual source type of the allow rule.
    /// </summary>
    /// <param name="rules">All firewall rules</param>
    /// <param name="allowRule">The 5G modem allow rule to check</param>
    /// <param name="mgmtNetwork">The management network (used for NETWORK-based sources, includes subnet for IP/CIDR matching)</param>
    /// <param name="externalZoneId">The external zone ID</param>
    /// <returns>True if the allow rule is eclipsed by a block rule</returns>
    private bool Is5GModemAllowRuleEclipsed(
        List<FirewallRule> rules,
        FirewallRule allowRule,
        NetworkInfo mgmtNetwork,
        string? externalZoneId)
    {
        // Determine source matching based on the allow rule's source type
        Func<FirewallRule, bool> sourceMatches;
        var sourceType = allowRule.SourceMatchingTarget?.ToUpperInvariant() ?? "";

        if (sourceType == "NETWORK")
        {
            // For network-based sources, use NetworkInfo overload which also checks IP/CIDR coverage
            sourceMatches = blockRule => AppliesToSourceNetwork(blockRule, mgmtNetwork);
        }
        else
        {
            // For IP, MAC, or ANY sources, use BlockRuleAffectsSameSource
            // which checks if the block rule affects the same source specification
            sourceMatches = blockRule => BlockRuleAffectsSameSource(blockRule, allowRule);
        }

        var eclipsingRule = rules.FirstOrDefault(r =>
            r.Enabled &&
            r.ActionType.IsBlockAction() &&
            r.Index < allowRule.Index &&
            sourceMatches(r) &&
            WouldBlockSameTraffic(r, allowRule, externalZoneId));

        if (eclipsingRule != null)
        {
            _logger.LogDebug("5G modem allow rule '{AllowRule}' (index {AllowIndex}, sourceType={SourceType}) eclipsed by block rule '{BlockRule}' (index {BlockIndex})",
                allowRule.Name, allowRule.Index, sourceType, eclipsingRule.Name, eclipsingRule.Index);
        }

        return eclipsingRule != null;
    }

    /// <summary>
    /// Determines if a block rule would actually block the traffic allowed by an allow rule.
    /// A narrow block rule (specific domains, IPs, ports) doesn't eclipse a broader allow rule
    /// targeting different destinations.
    /// </summary>
    private static bool WouldBlockSameTraffic(FirewallRule blockRule, FirewallRule allowRule, string? externalZoneId)
    {
        // If the allow rule targets specific web domains (like ui.com),
        // only block rules that affect HTTPS traffic to those domains would eclipse it
        if (allowRule.WebDomains?.Any() == true)
        {
            // Block rule with WEB destination only eclipses if it blocks the same domains
            if (string.Equals(blockRule.DestinationMatchingTarget, "WEB", StringComparison.OrdinalIgnoreCase))
            {
                // If block rule has no specific domains, it's a broad block
                if (blockRule.WebDomains == null || !blockRule.WebDomains.Any())
                    return true;

                // Check if any blocked domain overlaps with allowed domains
                return allowRule.WebDomains.Any(allowDomain =>
                    blockRule.WebDomains.Any(blockDomain =>
                        allowDomain.Contains(blockDomain, StringComparison.OrdinalIgnoreCase) ||
                        blockDomain.Contains(allowDomain, StringComparison.OrdinalIgnoreCase)));
            }

            // For other destination types (ANY, NETWORK, IP), check if block rule would block HTTPS
            // Web traffic uses TCP 443 - if block rule doesn't block that, it doesn't eclipse
            if (!FirewallGroupHelper.RuleBlocksPortAndProtocol(blockRule, "443", "tcp"))
                return false;

            // Block rule blocks HTTPS - but only eclipses if it targets External zone
            // A rule with destTarget=ANY but destZone=Internal doesn't block external web traffic
            return TargetsExternalZone(blockRule, externalZoneId);
        }

        // For non-WEB allow rules:
        // Block rule with destination=ANY blocks everything
        if (string.Equals(blockRule.DestinationMatchingTarget, "ANY", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check if block rule targets the same destination zone
        if (TargetsExternalZone(blockRule, externalZoneId))
            return true;

        return false;
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
    /// Uses FirewallRuleEvaluator to account for rule ordering - an allow rule with
    /// lower index could eclipse a block rule, meaning internet is not actually blocked.
    /// </summary>
    private bool IsInternetBlockedViaFirewall(
        NetworkInfo network,
        List<FirewallRule> firewallRules,
        string externalZoneId)
    {
        // Use FirewallRuleEvaluator to find the effective rule for internet traffic
        // Predicate matches rules that target this network's internet access
        var evalResult = FirewallRuleEvaluator.Evaluate(firewallRules, rule =>
            MatchesInternetTrafficPattern(rule, network.Id, externalZoneId));

        if (evalResult.IsBlocked)
        {
            _logger.LogDebug(
                "Network '{NetworkName}' has internet blocked by rule '{RuleName}' (index={Index})",
                network.Name, evalResult.EffectiveRule!.Name, evalResult.EffectiveRule.Index);
            return true;
        }

        if (evalResult.BlockRuleEclipsed)
        {
            _logger.LogDebug(
                "Network '{NetworkName}' has block rule '{BlockRule}' eclipsed by allow rule '{AllowRule}'",
                network.Name, evalResult.EclipsedBlockRule?.Name, evalResult.EffectiveRule?.Name);
        }

        return false;
    }

    /// <summary>
    /// Check if a rule matches the pattern for blocking internet access from a specific network.
    /// </summary>
    private static bool MatchesInternetTrafficPattern(FirewallRule rule, string networkId, string externalZoneId)
    {
        // Source must be NETWORK type with this network's ID in the list
        if (!string.Equals(rule.SourceMatchingTarget, "NETWORK", StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.SourceNetworkIds == null || !rule.SourceNetworkIds.Contains(networkId))
            return false;

        // Destination zone must be the External zone
        if (!string.Equals(rule.DestinationZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase))
            return false;

        // Destination must target ANY (all destinations in the zone)
        if (!string.Equals(rule.DestinationMatchingTarget, "ANY", StringComparison.OrdinalIgnoreCase))
            return false;

        // Protocol must be "all" to affect ALL traffic (not just specific ports/protocols)
        if (!string.Equals(rule.Protocol, "all", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
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
            return "External Access";
        }

        // Check for inter-VLAN isolation rules (blocking network-to-network or any-to-network)
        // Use "Source -> Destination" format for clear direction indication
        if (destTarget == "NETWORK" || srcTarget == "NETWORK")
        {
            return GetSourceToDestinationDescription(allowRule, networks);
        }

        // Default for other patterns (including Gateway zone blocks)
        return "";
    }

    /// <summary>
    /// Gets a "Source -> Destination" description for a firewall rule.
    /// Returns format like "Main Network -> Management" for grouping and display.
    /// </summary>
    private static string GetSourceToDestinationDescription(FirewallRule rule, List<NetworkInfo>? networks)
    {
        if (networks == null || networks.Count == 0)
            return "";

        var sourceName = GetNetworkPurposeFromRule(rule, networks, isSource: true);
        var destName = GetNetworkPurposeFromRule(rule, networks, isSource: false);

        // If we have both, format as "Source -> Dest"
        if (!string.IsNullOrEmpty(sourceName) && !string.IsNullOrEmpty(destName))
            return $"{sourceName} -> {destName}";

        // If we only have source
        if (!string.IsNullOrEmpty(sourceName))
            return $"{sourceName} ->";

        // If we only have destination, use "Device(s)" for unknown source
        if (!string.IsNullOrEmpty(destName))
            return $"Device(s) -> {destName}";

        return "";
    }

    /// <summary>
    /// Gets the network purpose(s) from a rule's source or destination.
    /// Returns purpose names like "IoT", "Security", "Management" for grouping.
    /// </summary>
    private static string? GetNetworkPurposeFromRule(FirewallRule rule, List<NetworkInfo> networks, bool isSource)
    {
        var target = isSource ? rule.SourceMatchingTarget : rule.DestinationMatchingTarget;
        var networkIds = isSource ? rule.SourceNetworkIds : rule.DestinationNetworkIds;
        var ips = isSource ? rule.SourceIps : rule.DestinationIps;

        // Check for ANY - represents all networks
        if (string.Equals(target, "ANY", StringComparison.OrdinalIgnoreCase))
            return null; // Don't include "Any" in the description

        var purposes = new HashSet<NetworkPurpose>();

        // Check for NETWORK target with network IDs
        if (string.Equals(target, "NETWORK", StringComparison.OrdinalIgnoreCase) &&
            networkIds != null && networkIds.Count > 0)
        {
            foreach (var networkId in networkIds)
            {
                var network = networks.FirstOrDefault(n =>
                    string.Equals(n.Id, networkId, StringComparison.OrdinalIgnoreCase));
                if (network != null)
                    purposes.Add(network.Purpose);
            }
        }

        // Check for IP target - find which network the IP belongs to
        if (string.Equals(target, "IP", StringComparison.OrdinalIgnoreCase) &&
            ips != null && ips.Count > 0)
        {
            foreach (var ipEntry in ips)
            {
                var ip = ipEntry.Contains('-') ? ipEntry.Split('-')[0] : ipEntry;
                if (ip.Contains('/'))
                    ip = ip.Split('/')[0];

                foreach (var network in networks)
                {
                    if (!string.IsNullOrEmpty(network.Subnet) &&
                        FirewallRuleOverlapDetector.IpMatchesCidr(ip, network.Subnet))
                    {
                        purposes.Add(network.Purpose);
                        break;
                    }
                }
            }
        }

        if (purposes.Count == 0)
            return null;

        // Convert purposes to display names, sorted for consistency
        var purposeNames = purposes
            .OrderBy(p => p)
            .Select(p => p switch
            {
                NetworkPurpose.IoT => "IoT",
                NetworkPurpose.Security => "Security",
                NetworkPurpose.Management => "Management",
                NetworkPurpose.Home => "Home",
                NetworkPurpose.Corporate => "Corporate",
                NetworkPurpose.Guest => "Guest",
                _ => p.ToString()
            })
            .ToList();

        return purposeNames.Count == 1 ? purposeNames[0] : string.Join(", ", purposeNames);
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

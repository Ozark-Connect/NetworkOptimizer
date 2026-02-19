using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Parses firewall rules from UniFi API responses.
/// Supports flattening of port lists (port groups) and IP lists (address groups)
/// when firewall groups are provided.
/// </summary>
public class FirewallRuleParser
{
    /// <summary>
    /// Synthetic zone ID for external/WAN zone (used for legacy rules without zone IDs)
    /// </summary>
    public const string LegacyExternalZoneId = "__LEGACY_EXTERNAL__";

    /// <summary>
    /// Synthetic zone ID for internal/LAN zone (used for legacy rules without zone IDs)
    /// </summary>
    public const string LegacyInternalZoneId = "__LEGACY_INTERNAL__";

    /// <summary>
    /// Synthetic zone ID for gateway/router zone (used for legacy rules without zone IDs)
    /// </summary>
    public const string LegacyGatewayZoneId = "__LEGACY_GATEWAY__";

    private readonly ILogger<FirewallRuleParser> _logger;
    private Dictionary<string, UniFiFirewallGroup>? _firewallGroups;

    public FirewallRuleParser(ILogger<FirewallRuleParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Set firewall groups for flattening port_group_id and ip_group_id references.
    /// Call this before ExtractFirewallPolicies to enable group resolution.
    /// </summary>
    public void SetFirewallGroups(IEnumerable<UniFiFirewallGroup>? groups)
    {
        if (groups == null)
        {
            _firewallGroups = null;
            return;
        }

        _firewallGroups = groups.ToDictionary(g => g.Id, g => g);
        _logger.LogDebug("Loaded {Count} firewall groups for rule flattening", _firewallGroups.Count);
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
    public FirewallRule? ParseFirewallPolicy(JsonElement policy)
    {
        // Generate an ID if not present or empty (allows parsing of simplified test data)
        var rawId = policy.GetStringOrNull("_id");
        var id = string.IsNullOrEmpty(rawId) ? Guid.NewGuid().ToString() : rawId;

        var name = policy.GetStringOrNull("name");
        var enabled = policy.GetBoolOrDefault("enabled", true);
        var action = policy.GetStringOrNull("action");
        var protocol = policy.GetStringOrNull("protocol");
        var matchOppositeProtocol = policy.GetBoolOrDefault("match_opposite_protocol", false);
        var index = policy.GetIntOrDefault("index", 0);
        var predefined = policy.GetBoolOrDefault("predefined", false);
        var icmpTypename = policy.GetStringOrNull("icmp_typename");

        // Extract connection state info
        var connectionStateType = policy.GetStringOrNull("connection_state_type");
        List<string>? connectionStates = null;
        if (policy.TryGetProperty("connection_states", out var connStates) && connStates.ValueKind == JsonValueKind.Array)
        {
            connectionStates = connStates.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }

        // Extract source info
        string? sourceMatchingTarget = null;
        List<string>? sourceNetworkIds = null;
        List<string>? sourceIps = null;
        List<string>? sourceClientMacs = null;
        string? sourcePort = null;
        string? sourceZoneId = null;
        bool sourceMatchOppositeIps = false;
        bool sourceMatchOppositeNetworks = false;
        bool sourceMatchOppositePorts = false;
        if (policy.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            sourceMatchingTarget = source.GetStringOrNull("matching_target");
            sourcePort = source.GetStringOrNull("port");
            sourceZoneId = source.GetStringOrNull("zone_id");
            sourceMatchOppositeIps = source.GetBoolOrDefault("match_opposite_ips", false);
            sourceMatchOppositeNetworks = source.GetBoolOrDefault("match_opposite_networks", false);
            sourceMatchOppositePorts = source.GetBoolOrDefault("match_opposite_ports", false);

            if (source.TryGetProperty("network_ids", out var netIds) && netIds.ValueKind == JsonValueKind.Array)
            {
                sourceNetworkIds = netIds.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }

            if (source.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Array)
            {
                sourceIps = ips.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }

            if (source.TryGetProperty("client_macs", out var macs) && macs.ValueKind == JsonValueKind.Array)
            {
                sourceClientMacs = macs.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }

            // Flatten IP group reference (matching_target_type == "OBJECT" with ip_group_id)
            var matchingTargetType = source.GetStringOrNull("matching_target_type");
            var ipGroupId = source.GetStringOrNull("ip_group_id");
            if (matchingTargetType == "OBJECT" && !string.IsNullOrEmpty(ipGroupId))
            {
                var groupIps = ResolveAddressGroup(ipGroupId);
                if (groupIps != null && groupIps.Count > 0)
                {
                    sourceIps = groupIps;
                    _logger.LogDebug("Flattened source IP group {GroupId} to {Count} addresses for rule {RuleName}",
                        ipGroupId, groupIps.Count, name);
                }
            }

            // Flatten port group reference (port_matching_type == "OBJECT" with port_group_id)
            var portMatchingType = source.GetStringOrNull("port_matching_type");
            var portGroupId = source.GetStringOrNull("port_group_id");
            if (portMatchingType == "OBJECT" && !string.IsNullOrEmpty(portGroupId))
            {
                var groupPorts = ResolvePortGroup(portGroupId);
                if (!string.IsNullOrEmpty(groupPorts))
                {
                    sourcePort = groupPorts;
                    _logger.LogDebug("Flattened source port group {GroupId} to '{Ports}' for rule {RuleName}",
                        portGroupId, groupPorts, name);
                }
            }
        }

        // Extract destination info including web domains and app IDs
        string? destPort = null;
        string? destMatchingTarget = null;
        List<string>? webDomains = null;
        List<string>? destNetworkIds = null;
        List<string>? destIps = null;
        List<int>? appIds = null;
        List<int>? appCategoryIds = null;
        string? destZoneId = null;
        bool destMatchOppositeIps = false;
        bool destMatchOppositeNetworks = false;
        bool destMatchOppositePorts = false;
        bool hasUnresolvedDestPortGroup = false;
        if (policy.TryGetProperty("destination", out var dest) && dest.ValueKind == JsonValueKind.Object)
        {
            destPort = dest.GetStringOrNull("port");
            destMatchingTarget = dest.GetStringOrNull("matching_target");
            destZoneId = dest.GetStringOrNull("zone_id");
            destMatchOppositeIps = dest.GetBoolOrDefault("match_opposite_ips", false);
            destMatchOppositeNetworks = dest.GetBoolOrDefault("match_opposite_networks", false);
            destMatchOppositePorts = dest.GetBoolOrDefault("match_opposite_ports", false);

            if (dest.TryGetProperty("web_domains", out var domains) && domains.ValueKind == JsonValueKind.Array)
            {
                webDomains = domains.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }

            if (dest.TryGetProperty("network_ids", out var netIds) && netIds.ValueKind == JsonValueKind.Array)
            {
                destNetworkIds = netIds.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }

            if (dest.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Array)
            {
                destIps = ips.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }

            // Extract app IDs for app-based matching (e.g., DNS, DoT, DoH blocking)
            if (dest.TryGetProperty("app_ids", out var appIdsArray) && appIdsArray.ValueKind == JsonValueKind.Array)
            {
                appIds = appIdsArray.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt32())
                    .ToList();
            }

            // Extract app category IDs for category-based matching (e.g., 13 = Web Services)
            if (dest.TryGetProperty("app_category_ids", out var appCategoryIdsArray) && appCategoryIdsArray.ValueKind == JsonValueKind.Array)
            {
                appCategoryIds = appCategoryIdsArray.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt32())
                    .ToList();
            }

            // Flatten IP group reference (matching_target_type == "OBJECT" with ip_group_id)
            var matchingTargetType = dest.GetStringOrNull("matching_target_type");
            var ipGroupId = dest.GetStringOrNull("ip_group_id");
            if (matchingTargetType == "OBJECT" && !string.IsNullOrEmpty(ipGroupId))
            {
                var groupIps = ResolveAddressGroup(ipGroupId);
                if (groupIps != null && groupIps.Count > 0)
                {
                    destIps = groupIps;
                    _logger.LogDebug("Flattened destination IP group {GroupId} to {Count} addresses for rule {RuleName}",
                        ipGroupId, groupIps.Count, name);
                }
            }

            // Flatten port group reference (port_matching_type == "OBJECT" with port_group_id)
            var portMatchingType = dest.GetStringOrNull("port_matching_type");
            var portGroupId = dest.GetStringOrNull("port_group_id");
            if (portMatchingType == "OBJECT" && !string.IsNullOrEmpty(portGroupId))
            {
                var groupPorts = ResolvePortGroup(portGroupId);
                if (!string.IsNullOrEmpty(groupPorts))
                {
                    destPort = groupPorts;
                    _logger.LogDebug("Flattened destination port group {GroupId} to '{Ports}' for rule {RuleName}",
                        portGroupId, groupPorts, name);
                }
                else
                {
                    hasUnresolvedDestPortGroup = true;
                    _logger.LogWarning("Failed to resolve destination port group {GroupId} for rule {RuleName} - group not found in {GroupCount} loaded groups",
                        portGroupId, name, _firewallGroups?.Count ?? 0);
                }
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
            MatchOppositeProtocol = matchOppositeProtocol,
            SourcePort = sourcePort,
            DestinationType = destMatchingTarget,
            DestinationPort = destPort,
            SourceNetworkIds = sourceNetworkIds,
            WebDomains = webDomains,
            Predefined = predefined,
            // Extended matching criteria
            SourceMatchingTarget = sourceMatchingTarget,
            SourceIps = sourceIps,
            SourceClientMacs = sourceClientMacs,
            DestinationMatchingTarget = destMatchingTarget,
            DestinationIps = destIps,
            DestinationNetworkIds = destNetworkIds,
            AppIds = appIds,
            AppCategoryIds = appCategoryIds,
            IcmpTypename = icmpTypename,
            // Zone and match opposite flags
            SourceZoneId = sourceZoneId,
            DestinationZoneId = destZoneId,
            SourceMatchOppositeIps = sourceMatchOppositeIps,
            SourceMatchOppositeNetworks = sourceMatchOppositeNetworks,
            SourceMatchOppositePorts = sourceMatchOppositePorts,
            DestinationMatchOppositeIps = destMatchOppositeIps,
            DestinationMatchOppositeNetworks = destMatchOppositeNetworks,
            DestinationMatchOppositePorts = destMatchOppositePorts,
            HasUnresolvedDestinationPortGroup = hasUnresolvedDestPortGroup,
            // Connection state matching
            ConnectionStateType = connectionStateType,
            ConnectionStates = connectionStates
        };
    }

    /// <summary>
    /// Parse a single firewall rule from JSON (legacy format)
    /// </summary>
    public FirewallRule? ParseFirewallRule(JsonElement rule)
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

        var srcNetworkConfId = rule.TryGetProperty("src_networkconf_id", out var srcNetConfProp)
            ? srcNetConfProp.GetString()
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

        var dstNetworkConfId = rule.TryGetProperty("dst_networkconf_id", out var dstNetConfProp)
            ? dstNetConfProp.GetString()
            : null;

        var destinationPort = rule.TryGetProperty("dst_port", out var dstPortProp)
            ? dstPortProp.GetString()
            : null;

        // Legacy rules may use dst_firewallgroup_ids for port groups and/or address groups
        List<string>? destIps = null;
        if (rule.TryGetProperty("dst_firewallgroup_ids", out var dstGroupIds) &&
            dstGroupIds.ValueKind == JsonValueKind.Array)
        {
            var resolvedPorts = new List<string>();
            var resolvedIps = new List<string>();
            foreach (var groupIdElement in dstGroupIds.EnumerateArray())
            {
                var groupId = groupIdElement.GetString();
                if (!string.IsNullOrEmpty(groupId))
                {
                    var resolvedPort = ResolvePortGroup(groupId);
                    if (!string.IsNullOrEmpty(resolvedPort))
                    {
                        resolvedPorts.Add(resolvedPort);
                    }

                    var resolvedAddresses = ResolveAddressGroup(groupId);
                    if (resolvedAddresses is { Count: > 0 })
                    {
                        resolvedIps.AddRange(resolvedAddresses);
                    }
                }
            }

            if (resolvedPorts.Count > 0 && string.IsNullOrEmpty(destinationPort))
            {
                destinationPort = string.Join(",", resolvedPorts);
                _logger.LogDebug("Resolved legacy rule destination ports from firewall groups: {Ports}", destinationPort);
            }

            if (resolvedIps.Count > 0)
            {
                destIps = resolvedIps;
            }
        }

        // Legacy rules may use src_firewallgroup_ids for address groups
        List<string>? sourceIps = null;
        if (rule.TryGetProperty("src_firewallgroup_ids", out var srcGroupIds) &&
            srcGroupIds.ValueKind == JsonValueKind.Array)
        {
            var resolvedIps = new List<string>();
            foreach (var groupIdElement in srcGroupIds.EnumerateArray())
            {
                var groupId = groupIdElement.GetString();
                if (!string.IsNullOrEmpty(groupId))
                {
                    var resolvedAddresses = ResolveAddressGroup(groupId);
                    if (resolvedAddresses is { Count: > 0 })
                    {
                        resolvedIps.AddRange(resolvedAddresses);
                    }
                }
            }

            if (resolvedIps.Count > 0)
            {
                sourceIps = resolvedIps;
            }
        }

        // Handle direct IPs in src_address and dst_address
        if (sourceIps == null && !string.IsNullOrEmpty(source) && source.Contains('.'))
        {
            sourceIps = new List<string> { source };
        }

        if (destIps == null && !string.IsNullOrEmpty(destination) && destination.Contains('.'))
        {
            destIps = new List<string> { destination };
        }

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
        // Fallback to flat format using src_networkconf_id, then src_network_id
        if (sourceNetworkIds == null)
        {
            if (!string.IsNullOrEmpty(srcNetworkConfId))
            {
                sourceNetworkIds = new List<string> { srcNetworkConfId };
            }
            else if (!string.IsNullOrEmpty(source) && !source.Contains('.'))
            {
                // source is from src_network_id (not an IP address from src_address)
                sourceNetworkIds = new List<string> { source };
            }
        }

        // Extract destination network IDs from dst_networkconf_id
        List<string>? destinationNetworkIds = null;
        if (!string.IsNullOrEmpty(dstNetworkConfId))
        {
            destinationNetworkIds = new List<string> { dstNetworkConfId };
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

        // Map legacy ruleset to zone IDs for compatibility with zone-based analysis
        var (sourceZoneId, destZoneId) = MapRulesetToZones(ruleset);

        // Determine matching targets based on resolved fields
        string? sourceMatchingTarget = null;
        if (sourceIps is { Count: > 0 })
            sourceMatchingTarget = "IP";
        else if (sourceNetworkIds is { Count: > 0 })
            sourceMatchingTarget = "NETWORK";

        string? destMatchingTarget = null;
        if (destIps is { Count: > 0 })
            destMatchingTarget = "IP";
        else if (destinationNetworkIds is { Count: > 0 })
            destMatchingTarget = "NETWORK";

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
            SourceMatchingTarget = sourceMatchingTarget,
            SourceIps = sourceIps,
            DestinationNetworkIds = destinationNetworkIds,
            DestinationMatchingTarget = destMatchingTarget,
            DestinationIps = destIps,
            WebDomains = webDomains,
            // Zone IDs derived from ruleset for compatibility with zone-based analysis
            SourceZoneId = sourceZoneId,
            DestinationZoneId = destZoneId
        };
    }

    /// <summary>
    /// Resolve an address group ID to a list of IP addresses/CIDRs/ranges
    /// </summary>
    private List<string>? ResolveAddressGroup(string groupId)
        => FirewallGroupHelper.ResolveAddressGroup(groupId, _firewallGroups, _logger);

    /// <summary>
    /// Resolve a port group ID to a comma-separated port string (e.g., "53,80,443" or "4001-4003")
    /// </summary>
    private string? ResolvePortGroup(string groupId)
        => FirewallGroupHelper.ResolvePortGroup(groupId, _firewallGroups, _logger);

    /// <summary>
    /// Maps legacy ruleset names to source and destination zone IDs.
    /// Legacy UniFi firewall rules use ruleset names like WAN_IN, WAN_OUT, LAN_IN, etc.
    /// instead of explicit zone IDs. This method derives synthetic zone IDs from the ruleset.
    /// </summary>
    /// <param name="ruleset">The legacy ruleset name (e.g., "WAN_IN", "WAN_OUT", "LAN_IN")</param>
    /// <returns>A tuple of (sourceZoneId, destinationZoneId), both may be null if ruleset is unknown</returns>
    public static (string? SourceZoneId, string? DestinationZoneId) MapRulesetToZones(string? ruleset)
    {
        if (string.IsNullOrEmpty(ruleset))
            return (null, null);

        // Normalize to uppercase for comparison
        return ruleset.ToUpperInvariant() switch
        {
            // WAN_OUT: Traffic from internal networks going to the internet
            // Most relevant for DNS security checks (blocking external DNS)
            "WAN_OUT" => (LegacyInternalZoneId, LegacyExternalZoneId),

            // WAN_IN: Traffic from internet coming into the network
            "WAN_IN" => (LegacyExternalZoneId, LegacyInternalZoneId),

            // LAN_IN: Traffic entering LAN interfaces (inter-VLAN traffic)
            "LAN_IN" => (LegacyInternalZoneId, LegacyInternalZoneId),

            // LAN_OUT: Traffic leaving LAN interfaces
            "LAN_OUT" => (LegacyInternalZoneId, null),

            // LAN_LOCAL: Traffic destined to the router/gateway itself
            "LAN_LOCAL" => (LegacyInternalZoneId, LegacyGatewayZoneId),

            // GUEST_IN: Traffic from guest networks
            "GUEST_IN" => (LegacyInternalZoneId, LegacyInternalZoneId),

            // GUEST_OUT: Traffic leaving guest networks
            "GUEST_OUT" => (LegacyInternalZoneId, null),

            // GUEST_LOCAL: Guest traffic to router
            "GUEST_LOCAL" => (LegacyInternalZoneId, LegacyGatewayZoneId),

            // Unknown ruleset
            _ => (null, null)
        };
    }

    /// <summary>
    /// Parse a combined traffic firewall rule (legacy format with app_ids at root level).
    /// These rules have NO protocol field - assume ALL protocols (TCP/UDP/ICMP).
    /// Used for app-based DNS blocking detection.
    /// </summary>
    /// <param name="rule">JSON element containing the combined traffic rule</param>
    /// <returns>A FirewallRule if this is an app-based rule, null otherwise</returns>
    public FirewallRule? ParseCombinedTrafficRule(JsonElement rule)
    {
        // Check for app-based rule (matching_target == "APP")
        var matchingTarget = rule.GetStringOrNull("matching_target");
        if (matchingTarget != "APP")
            return null; // Skip non-app rules (domain rules, etc.)

        // Extract app_ids from root level
        List<int>? appIds = null;
        if (rule.TryGetProperty("app_ids", out var appIdsArray) && appIdsArray.ValueKind == JsonValueKind.Array)
        {
            appIds = appIdsArray.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => e.GetInt32())
                .ToList();
        }

        if (appIds == null || appIds.Count == 0)
            return null;

        // Extract action (traffic_rule_action in legacy format)
        var action = rule.GetStringOrNull("traffic_rule_action")?.ToLowerInvariant();
        var enabled = rule.GetBoolOrDefault("enabled", true);
        var name = rule.GetStringOrNull("name");
        var originId = rule.GetStringOrNull("origin_id") ?? Guid.NewGuid().ToString();

        // Extract traffic_direction - this tells us the actual traffic flow direction
        // "TO" = outbound to external, "FROM" = inbound from external
        var trafficDirection = rule.GetStringOrNull("traffic_direction")?.ToUpperInvariant();

        // Extract ruleset from firewall_rule_details (for logging/debugging)
        string? ruleset = null;
        if (rule.TryGetProperty("firewall_rule_details", out var details) && details.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in details.EnumerateArray())
            {
                var detailRuleset = detail.GetStringOrNull("ruleset");
                if (!string.IsNullOrEmpty(detailRuleset))
                {
                    // Prefer IPv4 ruleset (skip v6 variants)
                    if (!detailRuleset.Contains("v6", StringComparison.OrdinalIgnoreCase))
                    {
                        ruleset = detailRuleset;
                        break;
                    }
                    // Keep as fallback if only v6 is available
                    ruleset ??= detailRuleset;
                }
            }
        }

        // For app-based traffic rules, use traffic_direction to determine zones
        // traffic_direction "TO" means blocking outbound traffic TO external destinations
        // traffic_direction "FROM" means blocking inbound traffic FROM external sources
        // This is more accurate than deriving from ruleset (which is often LAN_IN for both)
        string? sourceZone;
        string? destZone;
        if (trafficDirection == "TO")
        {
            // Outbound blocking: source is internal, destination is external
            sourceZone = LegacyInternalZoneId;
            destZone = LegacyExternalZoneId;
            _logger.LogDebug("App-based rule '{Name}' has traffic_direction=TO, setting destZone to External", name);
        }
        else if (trafficDirection == "FROM")
        {
            // Inbound blocking: source is external, destination is internal
            sourceZone = LegacyExternalZoneId;
            destZone = LegacyInternalZoneId;
            _logger.LogDebug("App-based rule '{Name}' has traffic_direction=FROM, setting sourceZone to External", name);
        }
        else
        {
            // Fallback to ruleset-based mapping if traffic_direction is not set
            (sourceZone, destZone) = MapRulesetToZones(ruleset);
            _logger.LogDebug("App-based rule '{Name}' has no traffic_direction, using ruleset '{Ruleset}' for zone mapping", name, ruleset);
        }

        return new FirewallRule
        {
            Id = originId,
            Name = name,
            Enabled = enabled,
            Action = action,
            Protocol = "all", // Legacy has NO protocol - assume all (TCP/UDP/ICMP)
            AppIds = appIds,
            DestinationMatchingTarget = matchingTarget,
            SourceZoneId = sourceZone,
            DestinationZoneId = destZone,
            Ruleset = ruleset
        };
    }

    /// <summary>
    /// Extract app-based rules from combined traffic firewall rules response.
    /// Only returns rules with matching_target == "APP" that have app IDs.
    /// </summary>
    /// <param name="root">JSON element containing the array of combined traffic rules</param>
    /// <returns>List of app-based FirewallRules</returns>
    public List<FirewallRule> ExtractCombinedTrafficRules(JsonElement root)
    {
        var rules = new List<FirewallRule>();

        if (root.ValueKind != JsonValueKind.Array)
            return rules;

        foreach (var element in root.EnumerateArray())
        {
            var rule = ParseCombinedTrafficRule(element);
            if (rule != null)
                rules.Add(rule);
        }

        _logger.LogDebug("Extracted {Count} app-based rules from combined traffic rules", rules.Count);
        return rules;
    }
}

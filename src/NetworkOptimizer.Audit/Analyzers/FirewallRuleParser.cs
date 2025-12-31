using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Parses firewall rules from UniFi API responses
/// </summary>
public class FirewallRuleParser
{
    private readonly ILogger<FirewallRuleParser> _logger;

    public FirewallRuleParser(ILogger<FirewallRuleParser> logger)
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
    public FirewallRule? ParseFirewallPolicy(JsonElement policy)
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
        var icmpTypename = policy.GetStringOrNull("icmp_typename");

        // Extract source info
        string? sourceMatchingTarget = null;
        List<string>? sourceNetworkIds = null;
        List<string>? sourceIps = null;
        List<string>? sourceClientMacs = null;
        string? sourcePort = null;
        string? sourceZoneId = null;
        bool sourceMatchOppositeIps = false;
        bool sourceMatchOppositeNetworks = false;
        if (policy.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            sourceMatchingTarget = source.GetStringOrNull("matching_target");
            sourcePort = source.GetStringOrNull("port");
            sourceZoneId = source.GetStringOrNull("zone_id");
            sourceMatchOppositeIps = source.GetBoolOrDefault("match_opposite_ips", false);
            sourceMatchOppositeNetworks = source.GetBoolOrDefault("match_opposite_networks", false);

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
        }

        // Extract destination info including web domains
        string? destPort = null;
        string? destMatchingTarget = null;
        List<string>? webDomains = null;
        List<string>? destNetworkIds = null;
        List<string>? destIps = null;
        string? destZoneId = null;
        bool destMatchOppositeIps = false;
        bool destMatchOppositeNetworks = false;
        bool destMatchOppositePorts = false;
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
        }

        return new FirewallRule
        {
            Id = id,
            Name = name,
            Enabled = enabled,
            Index = index,
            Action = action,
            Protocol = protocol,
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
            IcmpTypename = icmpTypename,
            // Zone and match opposite flags
            SourceZoneId = sourceZoneId,
            DestinationZoneId = destZoneId,
            SourceMatchOppositeIps = sourceMatchOppositeIps,
            SourceMatchOppositeNetworks = sourceMatchOppositeNetworks,
            DestinationMatchOppositeIps = destMatchOppositeIps,
            DestinationMatchOppositeNetworks = destMatchOppositeNetworks,
            DestinationMatchOppositePorts = destMatchOppositePorts
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
}

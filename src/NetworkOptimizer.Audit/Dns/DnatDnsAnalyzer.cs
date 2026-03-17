using System.Net;
using System.Text.Json;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Result of DNAT DNS coverage analysis
/// </summary>
public class DnatCoverageResult
{
    /// <summary>
    /// Whether any DNAT DNS rules exist (enabled, UDP port 53)
    /// </summary>
    public bool HasDnatDnsRules { get; set; }

    /// <summary>
    /// Whether DNAT rules provide full coverage across all DHCP-enabled networks
    /// </summary>
    public bool HasFullCoverage { get; set; }

    /// <summary>
    /// Network IDs that have DNAT DNS coverage
    /// </summary>
    public List<string> CoveredNetworkIds { get; } = new();

    /// <summary>
    /// Network IDs that lack DNAT DNS coverage
    /// </summary>
    public List<string> UncoveredNetworkIds { get; } = new();

    /// <summary>
    /// Network names that have DNAT DNS coverage
    /// </summary>
    public List<string> CoveredNetworkNames { get; } = new();

    /// <summary>
    /// Network names that lack DNAT DNS coverage
    /// </summary>
    public List<string> UncoveredNetworkNames { get; } = new();

    /// <summary>
    /// Network names that were excluded from coverage checks (by VLAN ID)
    /// </summary>
    public List<string> ExcludedNetworkNames { get; } = new();

    /// <summary>
    /// Single IP addresses used in DNAT rules (abnormal configuration)
    /// </summary>
    public List<string> SingleIpRules { get; } = new();

    /// <summary>
    /// The IP address DNS traffic is redirected to (from first matching rule)
    /// </summary>
    public string? RedirectTargetIp { get; set; }

    /// <summary>
    /// Parsed DNAT rules targeting DNS
    /// </summary>
    public List<DnatRuleInfo> Rules { get; } = new();
}

/// <summary>
/// Information about a parsed DNAT rule
/// </summary>
public class DnatRuleInfo
{
    /// <summary>
    /// Rule ID from UniFi
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Rule description
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Coverage type: "network", "subnet", "single_ip", "inverted_address", "interface"
    /// </summary>
    public required string CoverageType { get; init; }

    /// <summary>
    /// Network conf ID (for network type)
    /// </summary>
    public string? NetworkId { get; init; }

    /// <summary>
    /// CIDR notation (for subnet type)
    /// </summary>
    public string? SubnetCidr { get; init; }

    /// <summary>
    /// Single IP address (for single_ip type)
    /// </summary>
    public string? SingleIp { get; init; }

    /// <summary>
    /// Target IP for DNS redirect
    /// </summary>
    public string? RedirectIp { get; init; }

    /// <summary>
    /// Interface/VLAN ID this rule applies to (from in_interface field).
    /// When set, this scopes the rule to traffic from that VLAN even if source is "any".
    /// </summary>
    public string? InInterface { get; init; }

    /// <summary>
    /// When true, the rule applies to all networks EXCEPT the specified NetworkId.
    /// This inverts the network matching logic.
    /// </summary>
    public bool MatchOpposite { get; init; }

    /// <summary>
    /// Destination address filter (if specified). When set without InvertDestinationAddress,
    /// the rule only matches traffic to specific IPs instead of all DNS traffic.
    /// </summary>
    public string? DestinationAddress { get; init; }

    /// <summary>
    /// When true, the destination address is inverted (matches traffic NOT going to the address).
    /// This is valid for DNS redirection as it catches bypass attempts.
    /// </summary>
    public bool InvertDestinationAddress { get; init; }

    /// <summary>
    /// Whether the destination filter is restricted (specific address without invert).
    /// A restricted destination means the rule only catches some DNS bypass attempts.
    /// </summary>
    public bool HasRestrictedDestination =>
        !string.IsNullOrEmpty(DestinationAddress) && !InvertDestinationAddress;
}

/// <summary>
/// Analyzes DNAT rules for DNS port 53 coverage across networks
/// </summary>
public class DnatDnsAnalyzer
{
    /// <summary>
    /// Analyze NAT rules for DNS DNAT coverage
    /// </summary>
    /// <param name="natRulesData">Raw NAT rules from UniFi API</param>
    /// <param name="networks">List of networks to check coverage against</param>
    /// <param name="excludedVlanIds">Optional VLAN IDs to exclude from coverage checks</param>
    /// <param name="firewallGroups">Optional firewall groups for resolving FIREWALL_GROUPS filter types</param>
    /// <returns>Coverage analysis result</returns>
    public DnatCoverageResult Analyze(JsonElement? natRulesData, List<NetworkInfo>? networks, List<int>? excludedVlanIds = null, Dictionary<string, UniFiFirewallGroup>? firewallGroups = null)
    {
        var result = new DnatCoverageResult();

        if (!natRulesData.HasValue || networks == null || networks.Count == 0)
        {
            return result;
        }

        // Check ALL networks for DNAT coverage (not just DHCP-enabled)
        // Any network can have devices making DNS queries, regardless of DHCP status
        // Filter out excluded VLAN IDs if specified
        var excludedVlanSet = excludedVlanIds?.ToHashSet() ?? new HashSet<int>();
        var allNetworks = networks
            .Where(n => !excludedVlanSet.Contains(n.VlanId))
            .ToList();

        // Track excluded networks for reference
        result.ExcludedNetworkNames.AddRange(
            networks.Where(n => excludedVlanSet.Contains(n.VlanId)).Select(n => n.Name));

        // Parse DNAT rules targeting UDP port 53
        var dnatDnsRules = ParseDnatDnsRules(natRulesData.Value, firewallGroups);
        result.Rules.AddRange(dnatDnsRules);
        result.HasDnatDnsRules = dnatDnsRules.Count > 0;

        if (dnatDnsRules.Count == 0)
        {
            // No DNAT DNS rules - all networks uncovered
            foreach (var network in allNetworks)
            {
                result.UncoveredNetworkIds.Add(network.Id);
                result.UncoveredNetworkNames.Add(network.Name);
            }
            return result;
        }

        // Set redirect target from first rule
        result.RedirectTargetIp = dnatDnsRules.FirstOrDefault()?.RedirectIp;

        // Track covered networks
        var coveredNetworkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in dnatDnsRules)
        {
            switch (rule.CoverageType)
            {
                case "network":
                    // Network reference - coverage depends on MatchOpposite
                    if (!string.IsNullOrEmpty(rule.NetworkId))
                    {
                        if (rule.MatchOpposite)
                        {
                            // Match Opposite: covers all networks EXCEPT the specified one
                            foreach (var network in allNetworks)
                            {
                                if (!string.Equals(network.Id, rule.NetworkId, StringComparison.OrdinalIgnoreCase))
                                {
                                    coveredNetworkIds.Add(network.Id);
                                }
                            }
                        }
                        else
                        {
                            // Normal: covers only the specified network
                            coveredNetworkIds.Add(rule.NetworkId);
                        }
                    }
                    break;

                case "interface":
                    // in_interface scoping - full coverage for that network
                    if (!string.IsNullOrEmpty(rule.NetworkId))
                    {
                        coveredNetworkIds.Add(rule.NetworkId);
                    }
                    break;

                case "subnet":
                    if (!string.IsNullOrEmpty(rule.SubnetCidr))
                    {
                        // Check which networks are covered by this subnet
                        foreach (var network in allNetworks)
                        {
                            if (!string.IsNullOrEmpty(network.Subnet) &&
                                CidrCoversSubnet(rule.SubnetCidr, network.Subnet))
                            {
                                coveredNetworkIds.Add(network.Id);
                            }
                        }
                    }
                    break;

                case "inverted_address":
                    // Inverted source address covers all networks (excludes only specific IPs)
                    foreach (var network in allNetworks)
                    {
                        coveredNetworkIds.Add(network.Id);
                    }
                    break;

                case "single_ip":
                    if (!string.IsNullOrEmpty(rule.SingleIp))
                    {
                        result.SingleIpRules.Add(rule.SingleIp);
                    }
                    break;
            }
        }

        // Categorize networks by coverage
        foreach (var network in allNetworks)
        {
            if (coveredNetworkIds.Contains(network.Id))
            {
                result.CoveredNetworkIds.Add(network.Id);
                result.CoveredNetworkNames.Add(network.Name);
            }
            else
            {
                result.UncoveredNetworkIds.Add(network.Id);
                result.UncoveredNetworkNames.Add(network.Name);
            }
        }

        result.HasFullCoverage = result.UncoveredNetworkIds.Count == 0;

        return result;
    }

    /// <summary>
    /// Parse NAT rules JSON and extract enabled DNAT rules targeting UDP port 53
    /// </summary>
    private List<DnatRuleInfo> ParseDnatDnsRules(JsonElement natRulesData, Dictionary<string, UniFiFirewallGroup>? firewallGroups)
    {
        var rules = new List<DnatRuleInfo>();

        if (natRulesData.ValueKind != JsonValueKind.Array)
        {
            return rules;
        }

        foreach (var rule in natRulesData.EnumerateArray())
        {
            // Check rule type is DNAT
            var type = rule.GetStringOrNull("type");
            if (!string.Equals(type, "DNAT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check enabled
            if (!rule.GetBoolOrDefault("enabled"))
            {
                continue;
            }

            // Check protocol includes UDP (DNS is primarily UDP)
            var protocol = rule.GetStringOrNull("protocol")?.ToLowerInvariant();
            if (!IncludesUdp(protocol))
            {
                continue;
            }

            // Check destination port is 53 (directly or via firewall group)
            var destFilter = rule.GetPropertyOrNull("destination_filter");
            if (destFilter == null)
            {
                continue;
            }

            if (!DestinationIncludesPort53(destFilter.Value, firewallGroups))
            {
                continue;
            }

            // Parse destination filter address and invert flag
            var destInvertAddress = destFilter.Value.GetBoolOrDefault("invert_address", false);
            // For NETWORK_CONF destination filters, match_opposite serves as the invert flag
            if (!destInvertAddress)
            {
                destInvertAddress = destFilter.Value.GetBoolOrDefault("match_opposite", false);
            }
            var destAddress = ResolveFilterAddress(destFilter.Value, firewallGroups);

            // This is a valid DNAT DNS rule - parse it
            var id = rule.GetStringOrNull("_id") ?? Guid.NewGuid().ToString();
            var description = rule.GetStringOrNull("description");
            var redirectIp = rule.GetStringOrNull("ip_address");
            var inInterface = rule.GetStringOrNull("in_interface");

            // Parse source filter to determine coverage type
            var sourceFilter = rule.GetPropertyOrNull("source_filter");
            var ruleInfo = ParseSourceFilter(sourceFilter, firewallGroups, id, description, redirectIp, inInterface, destAddress, destInvertAddress);

            if (ruleInfo != null)
            {
                rules.Add(ruleInfo);
            }
        }

        return rules;
    }

    /// <summary>
    /// Check if the destination filter includes port 53, either directly or via a firewall port group
    /// </summary>
    private static bool DestinationIncludesPort53(JsonElement destFilter, Dictionary<string, UniFiFirewallGroup>? firewallGroups)
    {
        // Check direct port field first
        var destPort = destFilter.GetStringOrNull("port");
        if (IncludesPort53(destPort))
        {
            return true;
        }

        // Check firewall group IDs for port groups containing port 53
        if (firewallGroups != null)
        {
            var groupIds = GetFirewallGroupIds(destFilter);
            foreach (var groupId in groupIds)
            {
                var resolvedPorts = FirewallGroupHelper.ResolvePortGroup(groupId, firewallGroups);
                if (resolvedPorts != null && FirewallGroupHelper.IncludesPort(resolvedPorts, "53"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolve the address from a filter - either a direct address field or from firewall address groups
    /// </summary>
    private static string? ResolveFilterAddress(JsonElement filter, Dictionary<string, UniFiFirewallGroup>? firewallGroups)
    {
        // Check direct address field
        var address = filter.GetStringOrNull("address");
        if (!string.IsNullOrEmpty(address))
        {
            return address;
        }

        // Check firewall address groups
        if (firewallGroups != null)
        {
            var groupIds = GetFirewallGroupIds(filter);
            foreach (var groupId in groupIds)
            {
                var addresses = FirewallGroupHelper.ResolveAddressGroup(groupId, firewallGroups);
                if (addresses != null && addresses.Count > 0)
                {
                    return string.Join(",", addresses);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extract firewall_group_ids array from a filter element
    /// </summary>
    private static List<string> GetFirewallGroupIds(JsonElement filter)
    {
        var ids = new List<string>();
        var groupIdsElement = filter.GetPropertyOrNull("firewall_group_ids");
        if (groupIdsElement?.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in groupIdsElement.Value.EnumerateArray())
            {
                var groupId = item.GetString();
                if (!string.IsNullOrEmpty(groupId))
                {
                    ids.Add(groupId);
                }
            }
        }
        return ids;
    }

    /// <summary>
    /// Parse source filter into a DnatRuleInfo, determining coverage type
    /// </summary>
    private static DnatRuleInfo? ParseSourceFilter(
        JsonElement? sourceFilter,
        Dictionary<string, UniFiFirewallGroup>? firewallGroups,
        string id, string? description, string? redirectIp, string? inInterface,
        string? destAddress, bool destInvertAddress)
    {
        var filterType = sourceFilter?.GetStringOrNull("filter_type");
        var networkConfId = sourceFilter?.GetStringOrNull("network_conf_id");
        var address = sourceFilter?.GetStringOrNull("address");
        // UniFi uses "match_opposite" on NETWORK_CONF filters and "invert_address" on address/group filters
        var isInverted = (sourceFilter?.GetBoolOrDefault("match_opposite", false) ?? false)
            || (sourceFilter?.GetBoolOrDefault("invert_address", false) ?? false);

        // NETWORK_CONF filter type - references a specific network
        if (string.Equals(filterType, "NETWORK_CONF", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(networkConfId))
        {
            return new DnatRuleInfo
            {
                Id = id,
                Description = description,
                CoverageType = "network",
                NetworkId = networkConfId,
                RedirectIp = redirectIp,
                InInterface = inInterface,
                MatchOpposite = isInverted,
                DestinationAddress = destAddress,
                InvertDestinationAddress = destInvertAddress
            };
        }

        // FIREWALL_GROUPS filter type - resolve address groups
        if (string.Equals(filterType, "FIREWALL_GROUPS", StringComparison.OrdinalIgnoreCase) && sourceFilter.HasValue)
        {
            // Resolve address groups to get the actual addresses
            var resolvedAddresses = new List<string>();
            if (firewallGroups != null)
            {
                var groupIds = GetFirewallGroupIds(sourceFilter.Value);
                foreach (var groupId in groupIds)
                {
                    var addresses = FirewallGroupHelper.ResolveAddressGroup(groupId, firewallGroups);
                    if (addresses != null)
                    {
                        resolvedAddresses.AddRange(addresses);
                    }
                }
            }

            if (isInverted)
            {
                // Inverted firewall group - covers everything EXCEPT the group members.
                // Typically the group contains DNS server IPs, so this covers all other devices.
                return new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "inverted_address",
                    SingleIp = resolvedAddresses.Count > 0 ? string.Join(",", resolvedAddresses) : null,
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    MatchOpposite = true,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }

            // Non-inverted firewall group with in_interface - treat as interface coverage
            if (!string.IsNullOrEmpty(inInterface))
            {
                return new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "interface",
                    NetworkId = inInterface,
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }

            // Non-inverted firewall group without in_interface - check resolved addresses
            if (resolvedAddresses.Count > 0)
            {
                // Check if all resolved addresses are CIDRs (network coverage)
                var allCidrs = resolvedAddresses.All(a => a.Contains('/'));
                if (allCidrs)
                {
                    // Use first CIDR for subnet coverage (simplified - could be multiple)
                    return new DnatRuleInfo
                    {
                        Id = id,
                        Description = description,
                        CoverageType = "subnet",
                        SubnetCidr = resolvedAddresses[0],
                        RedirectIp = redirectIp,
                        InInterface = inInterface,
                        DestinationAddress = destAddress,
                        InvertDestinationAddress = destInvertAddress
                    };
                }

                // Single IPs in group
                return new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "single_ip",
                    SingleIp = resolvedAddresses[0],
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }

            // Couldn't resolve group - fall through to in_interface check
        }

        // Direct address in source filter
        if (!string.IsNullOrEmpty(address))
        {
            if (isInverted)
            {
                // Inverted source address - covers everything EXCEPT this address.
                // e.g., "not 192.168.1.220" means all devices except the DNS server itself.
                return new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "inverted_address",
                    SingleIp = address,
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    MatchOpposite = true,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }

            if (address.Contains('/'))
            {
                return new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "subnet",
                    SubnetCidr = address,
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }

            // Single IP (abnormal)
            return new DnatRuleInfo
            {
                Id = id,
                Description = description,
                CoverageType = "single_ip",
                SingleIp = address,
                RedirectIp = redirectIp,
                InInterface = inInterface,
                DestinationAddress = destAddress,
                InvertDestinationAddress = destInvertAddress
            };
        }

        // Source is "any" but in_interface scopes to a specific VLAN
        if (!string.IsNullOrEmpty(inInterface))
        {
            return new DnatRuleInfo
            {
                Id = id,
                Description = description,
                CoverageType = "interface",
                NetworkId = inInterface,
                RedirectIp = redirectIp,
                InInterface = inInterface,
                DestinationAddress = destAddress,
                InvertDestinationAddress = destInvertAddress
            };
        }

        return null; // Unknown filter type and no in_interface
    }

    /// <summary>
    /// Check if protocol includes UDP
    /// </summary>
    private static bool IncludesUdp(string? protocol)
    {
        if (string.IsNullOrEmpty(protocol))
        {
            return false;
        }

        return protocol switch
        {
            "udp" => true,
            "tcp_udp" => true,
            "all" => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if port specification includes port 53
    /// </summary>
    private static bool IncludesPort53(string? port)
    {
        if (string.IsNullOrEmpty(port))
        {
            return false;
        }

        // Could be "53", "53,443", "1:100" (range), etc.
        if (port == "53")
        {
            return true;
        }

        // Check comma-separated list
        var ports = port.Split(',');
        foreach (var p in ports)
        {
            var trimmed = p.Trim();
            if (trimmed == "53")
            {
                return true;
            }

            // Check for range (e.g., "1:100")
            if (trimmed.Contains(':'))
            {
                var range = trimmed.Split(':');
                if (range.Length == 2 &&
                    int.TryParse(range[0], out var start) &&
                    int.TryParse(range[1], out var end))
                {
                    if (start <= 53 && 53 <= end)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Check if a CIDR block covers another subnet.
    /// Delegates to NetworkUtilities.CidrCoversSubnet.
    /// </summary>
    public static bool CidrCoversSubnet(string ruleCidr, string networkSubnet)
        => NetworkUtilities.CidrCoversSubnet(ruleCidr, networkSubnet);
}

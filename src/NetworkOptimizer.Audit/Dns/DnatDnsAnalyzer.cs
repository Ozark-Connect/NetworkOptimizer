using System.Net;
using System.Text.Json;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

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
    /// Coverage type: "network", "subnet", or "single_ip"
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
    /// <returns>Coverage analysis result</returns>
    public DnatCoverageResult Analyze(JsonElement? natRulesData, List<NetworkInfo>? networks, List<int>? excludedVlanIds = null)
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
        var dnatDnsRules = ParseDnatDnsRules(natRulesData.Value);
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
    private List<DnatRuleInfo> ParseDnatDnsRules(JsonElement natRulesData)
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

            // Check destination port is 53
            var destFilter = rule.GetPropertyOrNull("destination_filter");
            if (destFilter == null)
            {
                continue;
            }

            var destPort = destFilter.Value.GetStringOrNull("port");
            if (!IncludesPort53(destPort))
            {
                continue;
            }

            // Parse destination filter address and invert flag
            var destAddress = destFilter.Value.GetStringOrNull("address");
            var destInvertAddress = destFilter.Value.GetBoolOrDefault("invert_address", false);

            // This is a valid DNAT DNS rule - parse it
            var id = rule.GetStringOrNull("_id") ?? Guid.NewGuid().ToString();
            var description = rule.GetStringOrNull("description");
            var redirectIp = rule.GetStringOrNull("ip_address");
            var inInterface = rule.GetStringOrNull("in_interface");

            // Parse source filter to determine coverage type
            var sourceFilter = rule.GetPropertyOrNull("source_filter");
            var filterType = sourceFilter?.GetStringOrNull("filter_type");
            var networkConfId = sourceFilter?.GetStringOrNull("network_conf_id");
            var address = sourceFilter?.GetStringOrNull("address");
            var matchOpposite = sourceFilter?.GetBoolOrDefault("match_opposite", false) ?? false;

            DnatRuleInfo ruleInfo;

            if (string.Equals(filterType, "NETWORK_CONF", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(networkConfId))
            {
                // Network reference - coverage depends on MatchOpposite
                // If MatchOpposite=false: covers only the specified network
                // If MatchOpposite=true: covers all networks EXCEPT the specified one
                ruleInfo = new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "network",
                    NetworkId = networkConfId,
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    MatchOpposite = matchOpposite,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }
            else if (!string.IsNullOrEmpty(address))
            {
                if (address.Contains('/'))
                {
                    // CIDR subnet
                    ruleInfo = new DnatRuleInfo
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
                else
                {
                    // Single IP (abnormal)
                    ruleInfo = new DnatRuleInfo
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
            }
            else if (!string.IsNullOrEmpty(inInterface))
            {
                // Source is "any" but in_interface scopes to a specific VLAN
                ruleInfo = new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "interface",
                    NetworkId = inInterface, // Use in_interface as the network ID for coverage
                    RedirectIp = redirectIp,
                    InInterface = inInterface,
                    DestinationAddress = destAddress,
                    InvertDestinationAddress = destInvertAddress
                };
            }
            else
            {
                continue; // Unknown filter type and no in_interface
            }

            rules.Add(ruleInfo);
        }

        return rules;
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

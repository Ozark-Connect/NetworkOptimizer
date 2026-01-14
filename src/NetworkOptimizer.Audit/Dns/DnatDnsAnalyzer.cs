using System.Net;
using System.Text.Json;
using NetworkOptimizer.Audit.Models;

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
    /// <returns>Coverage analysis result</returns>
    public DnatCoverageResult Analyze(JsonElement? natRulesData, List<NetworkInfo>? networks)
    {
        var result = new DnatCoverageResult();

        if (!natRulesData.HasValue || networks == null || networks.Count == 0)
        {
            return result;
        }

        // Check ALL networks for DNAT coverage (not just DHCP-enabled)
        // Any network can have devices making DNS queries, regardless of DHCP status
        var allNetworks = networks.ToList();

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
                case "interface":
                    // Network reference or in_interface scoping - full coverage for that network
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

            DnatRuleInfo ruleInfo;

            if (string.Equals(filterType, "NETWORK_CONF", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(networkConfId))
            {
                // Network reference - full coverage for that network
                ruleInfo = new DnatRuleInfo
                {
                    Id = id,
                    Description = description,
                    CoverageType = "network",
                    NetworkId = networkConfId,
                    RedirectIp = redirectIp,
                    InInterface = inInterface
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
                        InInterface = inInterface
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
                        InInterface = inInterface
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
                    InInterface = inInterface
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
    /// Check if a CIDR block covers another subnet
    /// </summary>
    /// <param name="ruleCidr">The DNAT rule's CIDR (e.g., "192.168.0.0/16")</param>
    /// <param name="networkSubnet">The network's subnet (e.g., "192.168.1.0/24")</param>
    /// <returns>True if ruleCidr completely covers networkSubnet</returns>
    public static bool CidrCoversSubnet(string ruleCidr, string networkSubnet)
    {
        try
        {
            var (ruleNetwork, rulePrefixLength) = ParseCidr(ruleCidr);
            var (subnetNetwork, subnetPrefixLength) = ParseCidr(networkSubnet);

            if (ruleNetwork == null || subnetNetwork == null)
            {
                return false;
            }

            // Rule must have same or shorter prefix (larger network) to cover subnet
            if (rulePrefixLength > subnetPrefixLength)
            {
                return false;
            }

            // Compare network addresses masked by rule's prefix length
            var ruleBytes = ruleNetwork.GetAddressBytes();
            var subnetBytes = subnetNetwork.GetAddressBytes();

            if (ruleBytes.Length != subnetBytes.Length)
            {
                return false; // IPv4 vs IPv6 mismatch
            }

            // Calculate how many full bytes and remaining bits to compare
            var fullBytes = rulePrefixLength / 8;
            var remainingBits = rulePrefixLength % 8;

            // Compare full bytes
            for (int i = 0; i < fullBytes; i++)
            {
                if (ruleBytes[i] != subnetBytes[i])
                {
                    return false;
                }
            }

            // Compare remaining bits if any
            if (remainingBits > 0 && fullBytes < ruleBytes.Length)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((ruleBytes[fullBytes] & mask) != (subnetBytes[fullBytes] & mask))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parse CIDR notation into network address and prefix length
    /// </summary>
    private static (IPAddress? network, int prefixLength) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            return (null, 0);
        }

        if (!IPAddress.TryParse(parts[0], out var address))
        {
            return (null, 0);
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return (null, 0);
        }

        return (address, prefixLength);
    }
}

/// <summary>
/// JSON helper extension methods for safe property access
/// </summary>
internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    public static bool GetBoolOrDefault(this JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (prop.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        return defaultValue;
    }

    public static JsonElement? GetPropertyOrNull(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            return prop;
        }
        return null;
    }
}

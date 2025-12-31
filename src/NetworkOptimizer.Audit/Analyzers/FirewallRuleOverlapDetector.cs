using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Static helper class for detecting overlap between firewall rules.
/// Two rules overlap only if ALL criteria (protocol, source, destination, port, ICMP type) have overlap.
/// </summary>
public static class FirewallRuleOverlapDetector
{
    /// <summary>
    /// Check if two rules could potentially overlap (match same traffic).
    /// Rules overlap only if ALL criteria have overlap.
    /// </summary>
    public static bool RulesOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        // First check zones - if zones differ, rules cannot overlap
        if (!ZonesOverlap(rule1, rule2))
            return false;

        return ProtocolsOverlap(rule1, rule2) &&
               SourcesOverlap(rule1, rule2) &&
               DestinationsOverlap(rule1, rule2) &&
               PortsOverlap(rule1, rule2) &&
               IcmpTypesOverlap(rule1, rule2);
    }

    /// <summary>
    /// Check if zones overlap. If both rules have different zone IDs, they cannot overlap.
    /// </summary>
    public static bool ZonesOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        // Check source zones
        var srcZone1 = rule1.SourceZoneId;
        var srcZone2 = rule2.SourceZoneId;
        if (!string.IsNullOrEmpty(srcZone1) && !string.IsNullOrEmpty(srcZone2) && srcZone1 != srcZone2)
            return false;

        // Check destination zones
        var dstZone1 = rule1.DestinationZoneId;
        var dstZone2 = rule2.DestinationZoneId;
        if (!string.IsNullOrEmpty(dstZone1) && !string.IsNullOrEmpty(dstZone2) && dstZone1 != dstZone2)
            return false;

        return true;
    }

    /// <summary>
    /// Check if protocols overlap (same protocol or either is "all")
    /// </summary>
    public static bool ProtocolsOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        var p1 = rule1.Protocol?.ToLowerInvariant() ?? "all";
        var p2 = rule2.Protocol?.ToLowerInvariant() ?? "all";

        // "all" matches everything
        if (p1 == "all" || p2 == "all")
            return true;

        // Same protocol
        if (p1 == p2)
            return true;

        // tcp_udp overlaps with tcp or udp
        if (p1 == "tcp_udp" && (p2 == "tcp" || p2 == "udp"))
            return true;
        if (p2 == "tcp_udp" && (p1 == "tcp" || p1 == "udp"))
            return true;

        return false;
    }

    /// <summary>
    /// Check if sources overlap (either is ANY, or networks/IPs intersect).
    /// Handles match_opposite_* flags which invert the matching.
    /// </summary>
    public static bool SourcesOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        var target1 = rule1.SourceMatchingTarget?.ToUpperInvariant() ?? "ANY";
        var target2 = rule2.SourceMatchingTarget?.ToUpperInvariant() ?? "ANY";

        // ANY matches everything
        if (target1 == "ANY" || target2 == "ANY")
            return true;

        // Different target types don't overlap (IP vs NETWORK)
        if (target1 != target2)
            return false;

        // Both are NETWORK - check for common network IDs
        if (target1 == "NETWORK")
        {
            var nets1 = rule1.SourceNetworkIds ?? new List<string>();
            var nets2 = rule2.SourceNetworkIds ?? new List<string>();
            var opposite1 = rule1.SourceMatchOppositeNetworks;
            var opposite2 = rule2.SourceMatchOppositeNetworks;

            return ListsOverlapWithOpposite(nets1, opposite1, nets2, opposite2, StringListsIntersect);
        }

        // Both are IP - check for overlapping IPs/CIDRs
        if (target1 == "IP")
        {
            var ips1 = rule1.SourceIps ?? new List<string>();
            var ips2 = rule2.SourceIps ?? new List<string>();
            var opposite1 = rule1.SourceMatchOppositeIps;
            var opposite2 = rule2.SourceMatchOppositeIps;

            return ListsOverlapWithOpposite(ips1, opposite1, ips2, opposite2, IpRangesOverlap);
        }

        return false;
    }

    /// <summary>
    /// Helper to check if two lists overlap considering match_opposite flags.
    /// When opposite=true, the list is INVERTED (matches "everyone EXCEPT these").
    /// </summary>
    private static bool ListsOverlapWithOpposite<T>(
        List<T> list1, bool opposite1,
        List<T> list2, bool opposite2,
        Func<List<T>, List<T>, bool> intersectFunc)
    {
        // Both normal (no inversion) - check for intersection
        if (!opposite1 && !opposite2)
        {
            return intersectFunc(list1, list2);
        }

        // Both inverted - they always overlap (both match "the rest of the world")
        if (opposite1 && opposite2)
        {
            return true;
        }

        // One inverted, one normal:
        // Rule with opposite=true matches "everyone EXCEPT list"
        // Rule with opposite=false matches "only list"
        // They overlap IF the normal list contains items NOT in the exception list
        var normalList = opposite1 ? list2 : list1;
        var exceptionList = opposite1 ? list1 : list2;

        // If all items in the normal list are in the exception list, no overlap
        // Otherwise, there's some overlap
        return !AllItemsInExceptionList(normalList, exceptionList);
    }

    /// <summary>
    /// Check if all items in normalList are contained in exceptionList
    /// </summary>
    private static bool AllItemsInExceptionList<T>(List<T> normalList, List<T> exceptionList)
    {
        if (typeof(T) == typeof(string))
        {
            var normal = normalList.Cast<string>().ToList();
            var exception = exceptionList.Cast<string>().ToList();

            // For IPs, need to check CIDR containment
            foreach (var item in normal)
            {
                bool found = exception.Any(e =>
                    e.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                    IpMatchesCidr(item, e) ||
                    IpMatchesCidr(e, item));
                if (!found)
                    return false;
            }
            return true;
        }

        return normalList.All(exceptionList.Contains);
    }

    /// <summary>
    /// Check if two string lists have any intersection
    /// </summary>
    private static bool StringListsIntersect(List<string> list1, List<string> list2)
    {
        return list1.Intersect(list2, StringComparer.OrdinalIgnoreCase).Any();
    }

    /// <summary>
    /// Check if destinations overlap (either is ANY, or networks/IPs/domains intersect).
    /// Handles match_opposite_* flags which invert the matching.
    /// </summary>
    public static bool DestinationsOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        var target1 = rule1.DestinationMatchingTarget?.ToUpperInvariant() ?? "ANY";
        var target2 = rule2.DestinationMatchingTarget?.ToUpperInvariant() ?? "ANY";

        // ANY matches everything
        if (target1 == "ANY" || target2 == "ANY")
            return true;

        // Different target types don't overlap (IP vs NETWORK vs WEB)
        if (target1 != target2)
            return false;

        // Both are NETWORK - check for common network IDs
        if (target1 == "NETWORK")
        {
            var nets1 = rule1.DestinationNetworkIds ?? new List<string>();
            var nets2 = rule2.DestinationNetworkIds ?? new List<string>();
            var opposite1 = rule1.DestinationMatchOppositeNetworks;
            var opposite2 = rule2.DestinationMatchOppositeNetworks;

            return ListsOverlapWithOpposite(nets1, opposite1, nets2, opposite2, StringListsIntersect);
        }

        // Both are IP - check for overlapping IPs/CIDRs
        if (target1 == "IP")
        {
            var ips1 = rule1.DestinationIps ?? new List<string>();
            var ips2 = rule2.DestinationIps ?? new List<string>();
            var opposite1 = rule1.DestinationMatchOppositeIps;
            var opposite2 = rule2.DestinationMatchOppositeIps;

            return ListsOverlapWithOpposite(ips1, opposite1, ips2, opposite2, IpRangesOverlap);
        }

        // Both are WEB - check for common domains
        if (target1 == "WEB")
        {
            var domains1 = rule1.WebDomains ?? new List<string>();
            var domains2 = rule2.WebDomains ?? new List<string>();
            return DomainsOverlap(domains1, domains2);
        }

        return false;
    }

    /// <summary>
    /// Check if ports overlap (either is ANY/empty, or ports intersect).
    /// Handles match_opposite_ports flag which inverts the matching.
    /// </summary>
    public static bool PortsOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        var protocol1 = rule1.Protocol?.ToLowerInvariant() ?? "all";
        var protocol2 = rule2.Protocol?.ToLowerInvariant() ?? "all";

        // Ports only matter for TCP/UDP
        var portProtocols = new[] { "tcp", "udp", "tcp_udp" };
        var rule1HasPorts = portProtocols.Contains(protocol1);
        var rule2HasPorts = portProtocols.Contains(protocol2);

        // If neither rule uses port-based protocol, ports don't matter
        if (!rule1HasPorts && !rule2HasPorts)
            return true;

        // If one uses "all" protocol, it matches any ports
        if (protocol1 == "all" || protocol2 == "all")
            return true;

        var port1 = rule1.DestinationPort;
        var port2 = rule2.DestinationPort;
        var opposite1 = rule1.DestinationMatchOppositePorts;
        var opposite2 = rule2.DestinationMatchOppositePorts;

        // Empty/null port means ANY (unless opposite is set, which would mean "no ports")
        if (string.IsNullOrEmpty(port1))
        {
            // If opposite1 is true with empty list, it matches ALL ports
            // If opposite1 is false with empty list, it also matches ALL ports
            return true;
        }
        if (string.IsNullOrEmpty(port2))
        {
            return true;
        }

        // Parse ports
        var ports1 = ParsePortString(port1);
        var ports2 = ParsePortString(port2);

        // Handle match_opposite logic
        return PortSetsOverlapWithOpposite(ports1, opposite1, ports2, opposite2);
    }

    /// <summary>
    /// Check if two port sets overlap considering match_opposite flags.
    /// </summary>
    private static bool PortSetsOverlapWithOpposite(HashSet<int> ports1, bool opposite1, HashSet<int> ports2, bool opposite2)
    {
        // Both normal - check for intersection
        if (!opposite1 && !opposite2)
        {
            return ports1.Intersect(ports2).Any();
        }

        // Both inverted - they always overlap (both match "other ports")
        if (opposite1 && opposite2)
        {
            return true;
        }

        // One inverted, one normal
        var normalPorts = opposite1 ? ports2 : ports1;
        var exceptionPorts = opposite1 ? ports1 : ports2;

        // They overlap if the normal set contains ports NOT in the exception set
        return normalPorts.Any(p => !exceptionPorts.Contains(p));
    }

    /// <summary>
    /// Check if ICMP types overlap (either is ANY, or same type)
    /// </summary>
    public static bool IcmpTypesOverlap(FirewallRule rule1, FirewallRule rule2)
    {
        var protocol1 = rule1.Protocol?.ToLowerInvariant() ?? "all";
        var protocol2 = rule2.Protocol?.ToLowerInvariant() ?? "all";

        // ICMP type only matters for ICMP protocol
        if (protocol1 != "icmp" && protocol2 != "icmp")
            return true;

        // If one rule is "all" protocol, it matches any ICMP type
        if (protocol1 == "all" || protocol2 == "all")
            return true;

        var icmp1 = rule1.IcmpTypename?.ToUpperInvariant() ?? "ANY";
        var icmp2 = rule2.IcmpTypename?.ToUpperInvariant() ?? "ANY";

        // ANY matches everything
        if (icmp1 == "ANY" || icmp2 == "ANY")
            return true;

        return icmp1 == icmp2;
    }

    /// <summary>
    /// Check if two lists of IP addresses/CIDRs have any overlap.
    /// </summary>
    public static bool IpRangesOverlap(List<string> ips1, List<string> ips2)
    {
        // Simple case: exact match on any IP/CIDR
        if (ips1.Intersect(ips2, StringComparer.OrdinalIgnoreCase).Any())
            return true;

        // Check if any IP in one list falls within a CIDR in the other
        foreach (var ip1 in ips1)
        {
            foreach (var ip2 in ips2)
            {
                if (IpMatchesCidr(ip1, ip2) || IpMatchesCidr(ip2, ip1))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check if an IP address or smaller CIDR falls within a larger CIDR.
    /// </summary>
    public static bool IpMatchesCidr(string ip, string cidr)
    {
        if (!cidr.Contains('/'))
            return false;

        try
        {
            var parts = cidr.Split('/');
            var networkAddress = parts[0];
            var prefixLength = int.Parse(parts[1]);

            // Extract the IP part (without CIDR suffix if present)
            var ipPart = ip.Contains('/') ? ip.Split('/')[0] : ip;

            // Parse both addresses
            var ipBytes = System.Net.IPAddress.Parse(ipPart).GetAddressBytes();
            var networkBytes = System.Net.IPAddress.Parse(networkAddress).GetAddressBytes();

            // Create mask
            var maskBytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int bitsInThisByte = Math.Max(0, Math.Min(8, prefixLength - (i * 8)));
                maskBytes[i] = (byte)(0xFF << (8 - bitsInThisByte));
            }

            // Check if masked addresses match
            for (int i = 0; i < 4; i++)
            {
                if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if two domain lists overlap (including subdomain matching)
    /// </summary>
    public static bool DomainsOverlap(List<string> domains1, List<string> domains2)
    {
        foreach (var d1 in domains1)
        {
            foreach (var d2 in domains2)
            {
                // Exact match
                if (d1.Equals(d2, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Subdomain match (one is subdomain of the other)
                if (d1.EndsWith("." + d2, StringComparison.OrdinalIgnoreCase) ||
                    d2.EndsWith("." + d1, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Check if two port strings overlap (handles ranges and comma-separated lists)
    /// </summary>
    public static bool PortStringsOverlap(string ports1, string ports2)
    {
        var set1 = ParsePortString(ports1);
        var set2 = ParsePortString(ports2);
        return set1.Intersect(set2).Any();
    }

    /// <summary>
    /// Parse a port string into a set of individual ports.
    /// Handles: "80", "80,443", "80-90", "80,443,8000-8080"
    /// </summary>
    public static HashSet<int> ParsePortString(string portString)
    {
        var ports = new HashSet<int>();

        foreach (var part in portString.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                // Range: "80-90"
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length == 2 &&
                    int.TryParse(rangeParts[0].Trim(), out var start) &&
                    int.TryParse(rangeParts[1].Trim(), out var end))
                {
                    for (int p = start; p <= end && p <= 65535; p++)
                        ports.Add(p);
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
    /// Compare the scope of two rules. Returns true if rule1 is significantly narrower than rule2.
    /// Used to detect "narrow exception before broad deny" patterns.
    /// </summary>
    public static bool IsNarrowerScope(FirewallRule rule1, FirewallRule rule2)
    {
        var sourceScore1 = GetSourceScopeScore(rule1);
        var sourceScore2 = GetSourceScopeScore(rule2);
        var destScore1 = GetDestinationScopeScore(rule1);
        var destScore2 = GetDestinationScopeScore(rule2);

        // Rule1 is narrower if it has a lower total scope score
        // A significantly narrower rule has at least 2 points difference, OR
        // one dimension is narrower and the other is not broader
        var totalScore1 = sourceScore1 + destScore1;
        var totalScore2 = sourceScore2 + destScore2;

        // If rule1's total is at least 2 points less, it's significantly narrower
        if (totalScore1 <= totalScore2 - 2)
            return true;

        // If source is narrower and destination is not broader (or vice versa)
        if (sourceScore1 < sourceScore2 && destScore1 <= destScore2)
            return true;
        if (destScore1 < destScore2 && sourceScore1 <= sourceScore2)
            return true;

        return false;
    }

    /// <summary>
    /// Calculate source scope score (lower = narrower, higher = broader)
    /// CLIENT (specific MACs) = 1
    /// IP (specific IPs, few) = 2
    /// IP (many IPs or CIDRs) = 3
    /// NETWORK (few networks) = 4
    /// NETWORK (many networks) = 5
    /// ANY = 10
    /// </summary>
    private static int GetSourceScopeScore(FirewallRule rule)
    {
        var target = rule.SourceMatchingTarget?.ToUpperInvariant() ?? "ANY";

        return target switch
        {
            "CLIENT" => 1 + GetListSizeBonus(rule.SourceClientMacs?.Count ?? 0),
            "IP" => 2 + GetListSizeBonus(rule.SourceIps?.Count ?? 0) + GetCidrBonus(rule.SourceIps),
            "NETWORK" => 4 + GetListSizeBonus(rule.SourceNetworkIds?.Count ?? 0),
            "ANY" => 10,
            _ => 10
        };
    }

    /// <summary>
    /// Calculate destination scope score (lower = narrower, higher = broader)
    /// WEB (few domains) = 1
    /// IP (specific IPs, few) = 2
    /// IP (many IPs or CIDRs) = 3
    /// NETWORK (few networks) = 4
    /// NETWORK (many networks) = 5
    /// ANY = 10
    /// </summary>
    private static int GetDestinationScopeScore(FirewallRule rule)
    {
        var target = rule.DestinationMatchingTarget?.ToUpperInvariant() ?? "ANY";

        return target switch
        {
            "WEB" => 1 + GetListSizeBonus(rule.WebDomains?.Count ?? 0),
            "IP" => 2 + GetListSizeBonus(rule.DestinationIps?.Count ?? 0) + GetCidrBonus(rule.DestinationIps),
            "NETWORK" => 4 + GetListSizeBonus(rule.DestinationNetworkIds?.Count ?? 0),
            "ANY" => 10,
            _ => 10
        };
    }

    /// <summary>
    /// Add a small bonus for larger lists (but cap it)
    /// </summary>
    private static int GetListSizeBonus(int count)
    {
        if (count <= 2) return 0;
        if (count <= 5) return 1;
        return 2;
    }

    /// <summary>
    /// Add bonus for CIDR ranges (they cover more IPs than single addresses)
    /// </summary>
    private static int GetCidrBonus(List<string>? ips)
    {
        if (ips == null || ips.Count == 0)
            return 0;

        // Check if any entry has a CIDR with a small prefix (large range)
        foreach (var ip in ips)
        {
            if (ip.Contains('/'))
            {
                var parts = ip.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[1], out var prefix))
                {
                    // /24 or smaller (larger range) adds more points
                    if (prefix <= 16) return 3;
                    if (prefix <= 24) return 2;
                    return 1;
                }
            }
        }
        return 0;
    }
}

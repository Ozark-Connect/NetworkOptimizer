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
        return ProtocolsOverlap(rule1, rule2) &&
               SourcesOverlap(rule1, rule2) &&
               DestinationsOverlap(rule1, rule2) &&
               PortsOverlap(rule1, rule2) &&
               IcmpTypesOverlap(rule1, rule2);
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
    /// Check if sources overlap (either is ANY, or networks/IPs intersect)
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
            return nets1.Intersect(nets2).Any();
        }

        // Both are IP - check for overlapping IPs/CIDRs
        if (target1 == "IP")
        {
            var ips1 = rule1.SourceIps ?? new List<string>();
            var ips2 = rule2.SourceIps ?? new List<string>();
            return IpRangesOverlap(ips1, ips2);
        }

        return false;
    }

    /// <summary>
    /// Check if destinations overlap (either is ANY, or networks/IPs/domains intersect)
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
            return nets1.Intersect(nets2).Any();
        }

        // Both are IP - check for overlapping IPs/CIDRs
        if (target1 == "IP")
        {
            var ips1 = rule1.DestinationIps ?? new List<string>();
            var ips2 = rule2.DestinationIps ?? new List<string>();
            return IpRangesOverlap(ips1, ips2);
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
    /// Check if ports overlap (either is ANY/empty, or ports intersect)
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

        // Empty/null port means ANY
        if (string.IsNullOrEmpty(port1) || string.IsNullOrEmpty(port2))
            return true;

        // Parse and compare ports
        return PortStringsOverlap(port1, port2);
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
}

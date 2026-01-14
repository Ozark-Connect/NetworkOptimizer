using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Shared helper for resolving firewall group references (port groups and address groups)
/// and checking port specifications.
/// </summary>
public static class FirewallGroupHelper
{
    /// <summary>
    /// Resolve a port group ID to a comma-separated port string (e.g., "53,80,443" or "4001-4003")
    /// </summary>
    public static string? ResolvePortGroup(
        string groupId,
        Dictionary<string, UniFiFirewallGroup>? firewallGroups,
        ILogger? logger = null)
    {
        if (firewallGroups == null || !firewallGroups.TryGetValue(groupId, out var group))
        {
            logger?.LogDebug("Port group {GroupId} not found in loaded groups", groupId);
            return null;
        }

        if (group.GroupType != "port-group")
        {
            logger?.LogWarning("Group {GroupId} ({GroupName}) is type '{GroupType}', expected port-group",
                groupId, group.Name, group.GroupType);
            return null;
        }

        if (group.GroupMembers == null || group.GroupMembers.Count == 0)
            return null;

        // Join port members with commas (they may be single ports like "53" or ranges like "4001-4003")
        return string.Join(",", group.GroupMembers);
    }

    /// <summary>
    /// Resolve an address group ID to a list of IP addresses/CIDRs/ranges
    /// </summary>
    public static List<string>? ResolveAddressGroup(
        string groupId,
        Dictionary<string, UniFiFirewallGroup>? firewallGroups,
        ILogger? logger = null)
    {
        if (firewallGroups == null || !firewallGroups.TryGetValue(groupId, out var group))
        {
            logger?.LogDebug("Address group {GroupId} not found in loaded groups", groupId);
            return null;
        }

        // Only resolve address groups (both IPv4 and IPv6), not port groups
        if (group.GroupType != "address-group" && group.GroupType != "ipv6-address-group")
        {
            logger?.LogWarning("Group {GroupId} ({GroupName}) is type '{GroupType}', expected address-group",
                groupId, group.Name, group.GroupType);
            return null;
        }

        // Both address-group and ipv6-address-group store their members in group_members
        return group.GroupMembers?.Count > 0 ? group.GroupMembers.ToList() : null;
    }

    /// <summary>
    /// Check if a port specification includes a specific port.
    /// Handles comma-separated lists and port ranges (e.g., "50-100").
    /// </summary>
    /// <param name="portSpec">Port specification string (e.g., "53", "80,443", "50-100", "53,80-90,443")</param>
    /// <param name="port">The port number to check for</param>
    /// <returns>True if the port specification includes the given port</returns>
    public static bool IncludesPort(string? portSpec, string port)
    {
        if (string.IsNullOrEmpty(portSpec) || !int.TryParse(port, out var targetPort))
            return false;

        // Split by comma and check each port or range in the list
        var parts = portSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            // Check for range (e.g., "50-100")
            var dashIndex = part.IndexOf('-');
            if (dashIndex > 0 && dashIndex < part.Length - 1)
            {
                var startStr = part.Substring(0, dashIndex);
                var endStr = part.Substring(dashIndex + 1);

                if (int.TryParse(startStr, out var rangeStart) &&
                    int.TryParse(endStr, out var rangeEnd) &&
                    targetPort >= rangeStart && targetPort <= rangeEnd)
                {
                    return true;
                }
            }
            else
            {
                // Exact match
                if (part == port)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Check if a rule allows a specific protocol, considering the match_opposite_protocol flag.
    /// For ALLOW rules: returns true if the target protocol is included in what's allowed.
    /// </summary>
    /// <param name="ruleProtocol">Protocol specified in the rule (e.g., "udp", "tcp", "tcp_udp", "icmp", "all")</param>
    /// <param name="matchOpposite">If true, the rule allows everything EXCEPT the specified protocol</param>
    /// <param name="targetProtocol">The protocol we want to know if it's allowed (e.g., "udp" or "tcp")</param>
    /// <returns>True if the rule effectively allows the target protocol</returns>
    public static bool AllowsProtocol(string? ruleProtocol, bool matchOpposite, string targetProtocol)
    {
        var protocol = ruleProtocol?.ToLowerInvariant() ?? "all";

        if (matchOpposite)
        {
            // Rule allows everything EXCEPT the specified protocol
            // So target is allowed if it's NOT the excluded protocol
            return !ProtocolIncludes(protocol, targetProtocol);
        }

        // Normal mode: rule allows the specified protocol(s)
        return ProtocolIncludes(protocol, targetProtocol);
    }

    /// <summary>
    /// Check if a protocol specification includes a target protocol.
    /// </summary>
    private static bool ProtocolIncludes(string protocol, string target)
    {
        return protocol switch
        {
            "all" => true,
            "tcp_udp" => target is "tcp" or "udp",
            _ => protocol == target
        };
    }

    /// <summary>
    /// Check if a firewall rule allows traffic on a specific port and protocol.
    /// Considers match_opposite_ports and match_opposite_protocol flags.
    /// </summary>
    /// <param name="rule">The firewall rule to check</param>
    /// <param name="port">The port number to check (e.g., "123" for NTP, "443" for HTTPS)</param>
    /// <param name="protocol">The protocol to check (e.g., "tcp", "udp")</param>
    /// <returns>True if the rule effectively allows traffic on the specified port and protocol</returns>
    public static bool RuleAllowsPortAndProtocol(Models.FirewallRule rule, string port, string protocol)
    {
        // Check if port is included and not inverted
        if (!IncludesPort(rule.DestinationPort, port))
            return false;

        if (rule.DestinationMatchOppositePorts)
            return false; // Port is excluded (inverted)

        // Check if protocol is allowed
        return AllowsProtocol(rule.Protocol, rule.MatchOppositeProtocol, protocol);
    }

    /// <summary>
    /// Check if a firewall rule blocks traffic on a specific port and protocol.
    /// Considers match_opposite_ports and match_opposite_protocol flags.
    /// </summary>
    /// <param name="rule">The firewall rule to check</param>
    /// <param name="port">The port number to check (e.g., "53" for DNS, "853" for DoT)</param>
    /// <param name="protocol">The protocol to check (e.g., "tcp", "udp")</param>
    /// <returns>True if the rule effectively blocks traffic on the specified port and protocol</returns>
    public static bool RuleBlocksPortAndProtocol(Models.FirewallRule rule, string port, string protocol)
    {
        // If ports are inverted, specified ports are NOT blocked
        if (rule.DestinationMatchOppositePorts)
        {
            if (IncludesPort(rule.DestinationPort, port))
                return false; // Port is explicitly excluded from blocking
        }
        else
        {
            // Normal mode - port must be specified to be blocked
            if (!IncludesPort(rule.DestinationPort, port))
                return false;
        }

        // Check if protocol is blocked
        return BlocksProtocol(rule.Protocol, rule.MatchOppositeProtocol, protocol);
    }

    /// <summary>
    /// Check if a rule blocks a specific protocol, considering the match_opposite_protocol flag.
    /// For BLOCK rules: returns true if the target protocol is included in what's blocked.
    /// </summary>
    public static bool BlocksProtocol(string? ruleProtocol, bool matchOpposite, string targetProtocol)
    {
        var protocol = ruleProtocol?.ToLowerInvariant() ?? "all";

        if (matchOpposite)
        {
            // Rule blocks everything EXCEPT the specified protocol
            // So target is blocked if it's NOT the excluded protocol
            return !ProtocolIncludes(protocol, targetProtocol);
        }

        // Normal mode: rule blocks the specified protocol(s)
        return ProtocolIncludes(protocol, targetProtocol);
    }
}

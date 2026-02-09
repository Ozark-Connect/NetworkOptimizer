using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Represents a firewall rule from UniFi configuration
/// </summary>
public class FirewallRule
{
    /// <summary>
    /// Rule ID
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Rule name/description
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Whether the rule is enabled
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Rule index/order
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Action (accept, drop, reject)
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Parsed action type enum (computed from Action string)
    /// </summary>
    public FirewallAction ActionType => FirewallActionExtensions.Parse(Action);

    /// <summary>
    /// Protocol (tcp, udp, all, etc.)
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// When true, protocol is INVERTED (means "all protocols EXCEPT this one")
    /// </summary>
    public bool MatchOppositeProtocol { get; init; }

    /// <summary>
    /// Source type (address, network, group, any)
    /// </summary>
    public string? SourceType { get; init; }

    /// <summary>
    /// Source address/network/group
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Source port
    /// </summary>
    public string? SourcePort { get; init; }

    /// <summary>
    /// Destination type (address, network, group, any)
    /// </summary>
    public string? DestinationType { get; init; }

    /// <summary>
    /// Destination address/network/group
    /// </summary>
    public string? Destination { get; init; }

    /// <summary>
    /// Destination port
    /// </summary>
    public string? DestinationPort { get; init; }

    /// <summary>
    /// Whether this rule has been hit (traffic matched)
    /// </summary>
    public bool HasBeenHit { get; init; }

    /// <summary>
    /// Hit count (if available)
    /// </summary>
    public long HitCount { get; init; }

    /// <summary>
    /// Ruleset (LAN_IN, WAN_OUT, etc.)
    /// </summary>
    public string? Ruleset { get; init; }

    /// <summary>
    /// Source network IDs (for network-based rules)
    /// </summary>
    public List<string>? SourceNetworkIds { get; init; }

    /// <summary>
    /// Destination web domains (for web filtering rules)
    /// </summary>
    public List<string>? WebDomains { get; init; }

    /// <summary>
    /// Application IDs for app-based matching (e.g., 589885 = DNS, 1310917 = DoT, 1310919 = DoH).
    /// Used when DestinationMatchingTarget is "APP".
    /// </summary>
    public List<int>? AppIds { get; init; }

    /// <summary>
    /// Application category IDs for category-based matching (e.g., 13 = Web Services, 19 = Network Protocols).
    /// Used when matching entire categories of applications.
    /// </summary>
    public List<int>? AppCategoryIds { get; init; }

    /// <summary>
    /// Whether this is a predefined/system rule (not user-created)
    /// </summary>
    public bool Predefined { get; init; }

    // === Extended Matching Criteria for Overlap Detection ===

    /// <summary>
    /// Source matching target type (ANY, IP, NETWORK)
    /// </summary>
    public string? SourceMatchingTarget { get; init; }

    /// <summary>
    /// Source IP addresses/CIDRs (when SourceMatchingTarget is IP)
    /// </summary>
    public List<string>? SourceIps { get; init; }

    /// <summary>
    /// Source client MAC addresses (when SourceMatchingTarget is CLIENT)
    /// </summary>
    public List<string>? SourceClientMacs { get; init; }

    /// <summary>
    /// Destination matching target type (ANY, IP, NETWORK, WEB)
    /// </summary>
    public string? DestinationMatchingTarget { get; init; }

    /// <summary>
    /// Destination IP addresses/CIDRs (when DestinationMatchingTarget is IP)
    /// </summary>
    public List<string>? DestinationIps { get; init; }

    /// <summary>
    /// Destination network IDs (when DestinationMatchingTarget is NETWORK)
    /// </summary>
    public List<string>? DestinationNetworkIds { get; init; }

    /// <summary>
    /// ICMP type name (ANY, ECHO_REQUEST, etc.) - for ICMP protocol
    /// </summary>
    public string? IcmpTypename { get; init; }

    // === Zone and Match Opposite Flags for Accurate Overlap Detection ===

    /// <summary>
    /// Source zone ID - rules with different zones cannot overlap
    /// </summary>
    public string? SourceZoneId { get; init; }

    /// <summary>
    /// Destination zone ID - rules with different zones cannot overlap
    /// </summary>
    public string? DestinationZoneId { get; init; }

    /// <summary>
    /// When true, source IPs are INVERTED (means "everyone EXCEPT these IPs")
    /// </summary>
    public bool SourceMatchOppositeIps { get; init; }

    /// <summary>
    /// When true, source networks are INVERTED (means "all networks EXCEPT these")
    /// </summary>
    public bool SourceMatchOppositeNetworks { get; init; }

    /// <summary>
    /// When true, source ports are INVERTED (means "all ports EXCEPT these")
    /// </summary>
    public bool SourceMatchOppositePorts { get; init; }

    /// <summary>
    /// When true, destination IPs are INVERTED (means "everyone EXCEPT these IPs")
    /// </summary>
    public bool DestinationMatchOppositeIps { get; init; }

    /// <summary>
    /// When true, destination networks are INVERTED (means "all networks EXCEPT these")
    /// </summary>
    public bool DestinationMatchOppositeNetworks { get; init; }

    /// <summary>
    /// When true, destination ports are INVERTED (means "all ports EXCEPT these")
    /// </summary>
    public bool DestinationMatchOppositePorts { get; init; }

    /// <summary>
    /// Whether this rule has an unresolved destination port group reference.
    /// When true, DestinationPort is null because the referenced port group could not be resolved,
    /// not because the rule intentionally targets all ports.
    /// </summary>
    public bool HasUnresolvedDestinationPortGroup { get; init; }

    // === Connection State Matching ===

    /// <summary>
    /// Connection state type: ALL, CUSTOM, or null (defaults to ALL behavior)
    /// </summary>
    public string? ConnectionStateType { get; init; }

    /// <summary>
    /// Specific connection states when ConnectionStateType is CUSTOM.
    /// Values: NEW, ESTABLISHED, RELATED, INVALID
    /// </summary>
    public List<string>? ConnectionStates { get; init; }

    /// <summary>
    /// Returns true if this rule blocks NEW connections (not just INVALID).
    /// Rules that only block INVALID connections don't provide inter-VLAN isolation.
    /// </summary>
    public bool BlocksNewConnections()
    {
        // If no connection state type specified, assume ALL (blocks everything including NEW)
        if (string.IsNullOrEmpty(ConnectionStateType))
            return true;

        // ALL means it blocks all connection states including NEW
        if (ConnectionStateType.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return true;

        // CUSTOM - check if NEW is in the list
        if (ConnectionStateType.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionStates?.Any(s =>
                s.Equals("NEW", StringComparison.OrdinalIgnoreCase)) == true;
        }

        // Unknown type - be conservative and assume it might block NEW
        return true;
    }

    /// <summary>
    /// Returns true if the source targets all addresses (ANY or unspecified).
    /// Handles both v2 API format (SourceMatchingTarget) and legacy format (SourceType/Source).
    /// </summary>
    public bool IsAnySource()
    {
        if (!string.IsNullOrEmpty(SourceMatchingTarget))
            return SourceMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase);

        return SourceType?.Equals("any", StringComparison.OrdinalIgnoreCase) == true
            || string.IsNullOrEmpty(Source);
    }

    /// <summary>
    /// Returns true if the destination targets all addresses (ANY or unspecified).
    /// Handles both v2 API format (DestinationMatchingTarget) and legacy format (DestinationType/Destination).
    /// </summary>
    public bool IsAnyDestination()
    {
        if (!string.IsNullOrEmpty(DestinationMatchingTarget))
            return DestinationMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase);

        return DestinationType?.Equals("any", StringComparison.OrdinalIgnoreCase) == true
            || string.IsNullOrEmpty(Destination);
    }

    /// <summary>
    /// Returns true if this rule allows NEW connections (not just ESTABLISHED/RELATED).
    /// Rules with RESPOND_ONLY only allow return traffic, not new connections.
    /// </summary>
    public bool AllowsNewConnections()
    {
        // If no connection state type specified, assume ALL (allows everything including NEW)
        if (string.IsNullOrEmpty(ConnectionStateType))
            return true;

        // ALL means it allows all connection states including NEW
        if (ConnectionStateType.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            return true;

        // RESPOND_ONLY means it only allows ESTABLISHED/RELATED, not NEW
        if (ConnectionStateType.Equals("RESPOND_ONLY", StringComparison.OrdinalIgnoreCase))
            return false;

        // CUSTOM - check if NEW is in the list
        if (ConnectionStateType.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase))
        {
            return ConnectionStates?.Any(s =>
                s.Equals("NEW", StringComparison.OrdinalIgnoreCase)) == true;
        }

        // Unknown type - be conservative and assume it might allow NEW
        return true;
    }

    /// <summary>
    /// Returns true if this rule applies to traffic from a specific source network.
    /// Checks zone matching, network ID matching (with Match Opposite), and IP/CIDR coverage.
    /// Handles v2 API format (SourceMatchingTarget) and legacy format (Source).
    /// </summary>
    public bool AppliesToSourceNetwork(NetworkInfo network)
    {
        // Zone check: if rule has a source zone and network has a zone, they must match
        if (!string.IsNullOrEmpty(SourceZoneId) && !string.IsNullOrEmpty(network.FirewallZoneId))
        {
            if (!string.Equals(SourceZoneId, network.FirewallZoneId, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // v2 API: Check SourceMatchingTarget
        if (!string.IsNullOrEmpty(SourceMatchingTarget))
        {
            if (SourceMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase))
                return true;

            if (SourceMatchingTarget.Equals("NETWORK", StringComparison.OrdinalIgnoreCase))
            {
                var networkIds = SourceNetworkIds ?? [];
                return SourceMatchOppositeNetworks
                    ? !networkIds.Contains(network.Id, StringComparer.OrdinalIgnoreCase)
                    : networkIds.Contains(network.Id, StringComparer.OrdinalIgnoreCase);
            }

            if (SourceMatchingTarget.Equals("IP", StringComparison.OrdinalIgnoreCase) &&
                SourceIps?.Count > 0 && !string.IsNullOrEmpty(network.Subnet))
            {
                var cidrCovers = NetworkUtilities.AnyCidrCoversSubnet(SourceIps, network.Subnet);
                return SourceMatchOppositeIps ? !cidrCovers : cidrCovers;
            }

            // CLIENT, etc. - doesn't match by network
            return false;
        }

        // Legacy: check SourceNetworkIds if populated
        if (SourceNetworkIds != null && SourceNetworkIds.Count > 0)
        {
            return SourceMatchOppositeNetworks
                ? !SourceNetworkIds.Contains(network.Id, StringComparer.OrdinalIgnoreCase)
                : SourceNetworkIds.Contains(network.Id, StringComparer.OrdinalIgnoreCase);
        }

        // Legacy fallback
        return string.Equals(Source, network.Id, StringComparison.OrdinalIgnoreCase);
    }
}

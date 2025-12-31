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
    /// Protocol (tcp, udp, all, etc.)
    /// </summary>
    public string? Protocol { get; init; }

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
}

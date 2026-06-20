namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Represents a single audit finding or issue
/// </summary>
public class AuditIssue
{
    /// <summary>
    /// Type of the issue (e.g., "IOT_WRONG_VLAN", "NO_MAC_RESTRICTION")
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Severity level of the issue
    /// </summary>
    public required AuditSeverity Severity { get; init; }

    /// <summary>
    /// Human-readable message describing the issue
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// General description for grouping similar issues (e.g., "Management to External Access")
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Switch/device name where the issue was found
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Port identifier (e.g., "1", "eth0", "Port 5")
    /// </summary>
    public string? Port { get; init; }

    /// <summary>
    /// Port name or label
    /// </summary>
    public string? PortName { get; init; }

    /// <summary>
    /// MAC address of the switch/device where the issue was found.
    /// Used for reliable device identification.
    /// </summary>
    public string? DeviceMac { get; init; }

    /// <summary>
    /// Current network/VLAN the port is on
    /// </summary>
    public string? CurrentNetwork { get; init; }

    /// <summary>
    /// Current VLAN ID
    /// </summary>
    public int? CurrentVlan { get; init; }

    /// <summary>
    /// Recommended network/VLAN to move to
    /// </summary>
    public string? RecommendedNetwork { get; init; }

    /// <summary>
    /// Recommended VLAN ID
    /// </summary>
    public int? RecommendedVlan { get; init; }

    /// <summary>
    /// Recommended action to remediate the issue
    /// </summary>
    public string? RecommendedAction { get; init; }

    /// <summary>
    /// Additional metadata about the issue
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Rule that generated this issue
    /// </summary>
    public string? RuleId { get; init; }

    /// <summary>
    /// Score impact (points deducted from security posture)
    /// </summary>
    public int ScoreImpact { get; init; }

    /// <summary>
    /// Client MAC address (for wireless issues)
    /// </summary>
    public string? ClientMac { get; init; }

    /// <summary>
    /// Client display name (for wireless issues)
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Access point name (for wireless issues)
    /// </summary>
    public string? AccessPoint { get; init; }

    /// <summary>
    /// WiFi band (2.4GHz, 5GHz, 6GHz) for wireless issues
    /// </summary>
    public string? WifiBand { get; init; }

    /// <summary>
    /// Whether this issue is for a wireless client
    /// </summary>
    public bool IsWireless { get; init; }

    /// <summary>
    /// Firewall or DNAT rules that contribute the partial coverage behind this finding,
    /// along with the networks each rule covers. Surfaced in the UI so users can see
    /// exactly which rules drive a "partial coverage" DNS finding.
    /// </summary>
    public IReadOnlyList<CoveringRuleInfo>? CoveringRules { get; init; }
}

/// <summary>
/// A single firewall or DNAT rule that provides partial coverage for a DNS finding,
/// paired with the networks it covers.
/// </summary>
public class CoveringRuleInfo
{
    /// <summary>
    /// Display name of the rule (firewall rule name or DNAT rule description).
    /// </summary>
    public required string RuleName { get; init; }

    /// <summary>
    /// Names of the networks this rule covers.
    /// </summary>
    public IReadOnlyList<string> CoveredNetworks { get; init; } = new List<string>();
}

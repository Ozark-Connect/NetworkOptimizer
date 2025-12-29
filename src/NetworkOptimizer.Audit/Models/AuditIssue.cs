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
    /// Whether this issue is for a wireless client
    /// </summary>
    public bool IsWireless { get; init; }
}

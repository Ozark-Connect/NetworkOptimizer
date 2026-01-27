using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Severity level for AP lock issues.
/// </summary>
public enum ApLockSeverity
{
    /// <summary>
    /// Mobile device locked to AP - user should review
    /// </summary>
    Warning,

    /// <summary>
    /// Stationary device locked to AP - informational only
    /// </summary>
    Info,

    /// <summary>
    /// Unknown device type - cannot determine if lock is appropriate
    /// </summary>
    Unknown
}

/// <summary>
/// Issue found with a client device locked to a specific AP.
/// </summary>
public class ApLockIssue
{
    /// <summary>
    /// MAC address of the client device
    /// </summary>
    public string ClientMac { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the client (hostname or friendly name)
    /// </summary>
    public string ClientName { get; set; } = string.Empty;

    /// <summary>
    /// MAC address of the AP the client is locked to
    /// </summary>
    public string LockedApMac { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the locked AP
    /// </summary>
    public string LockedApName { get; set; } = string.Empty;

    /// <summary>
    /// Device detection result for the client
    /// </summary>
    public DeviceDetectionResult DeviceDetection { get; set; } = new();

    /// <summary>
    /// Number of times this client has roamed (mobility indicator)
    /// </summary>
    public int? RoamCount { get; set; }

    /// <summary>
    /// Whether this client is currently offline
    /// </summary>
    public bool IsOffline { get; set; }

    /// <summary>
    /// When this client was last seen (for offline clients)
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Severity of this issue based on device type
    /// </summary>
    public ApLockSeverity Severity { get; set; }

    /// <summary>
    /// Human-readable recommendation
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;
}

namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Represents a trunk link between two network devices.
/// </summary>
public class TrunkLink
{
    /// <summary>
    /// MAC address of device A
    /// </summary>
    public string DeviceAMac { get; set; } = string.Empty;

    /// <summary>
    /// Display name of device A
    /// </summary>
    public string DeviceAName { get; set; } = string.Empty;

    /// <summary>
    /// Port number on device A
    /// </summary>
    public int DeviceAPort { get; set; }

    /// <summary>
    /// MAC address of device B
    /// </summary>
    public string DeviceBMac { get; set; } = string.Empty;

    /// <summary>
    /// Display name of device B
    /// </summary>
    public string DeviceBName { get; set; } = string.Empty;

    /// <summary>
    /// Port number on device B
    /// </summary>
    public int DeviceBPort { get; set; }

    /// <summary>
    /// Network IDs allowed on device A's port
    /// </summary>
    public HashSet<string> DeviceAAllowedVlans { get; set; } = new();

    /// <summary>
    /// Network IDs allowed on device B's port
    /// </summary>
    public HashSet<string> DeviceBAllowedVlans { get; set; } = new();
}

/// <summary>
/// Represents a VLAN mismatch between two sides of a trunk link.
/// </summary>
public class VlanMismatch
{
    /// <summary>
    /// Network config ID of the mismatched VLAN
    /// </summary>
    public string NetworkId { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the network
    /// </summary>
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>
    /// VLAN tag number
    /// </summary>
    public int VlanId { get; set; }

    /// <summary>
    /// Purpose of this network (for context in recommendations)
    /// e.g., "corporate", "guest", "vlan-only"
    /// </summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// Which side is missing the VLAN ("A" or "B")
    /// </summary>
    public string MissingSide { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the side missing the VLAN
    /// </summary>
    public string MissingSideName { get; set; } = string.Empty;
}

/// <summary>
/// Issue found in trunk VLAN configuration - VLANs allowed on one side but not the other.
/// </summary>
public class TrunkConsistencyIssue
{
    /// <summary>
    /// The trunk link with the inconsistency
    /// </summary>
    public TrunkLink Link { get; set; } = new();

    /// <summary>
    /// List of VLAN mismatches found on this link
    /// </summary>
    public List<VlanMismatch> Mismatches { get; set; } = new();

    /// <summary>
    /// Confidence level based on how common the VLANs are across other trunks
    /// </summary>
    public DiagnosticConfidence Confidence { get; set; }

    /// <summary>
    /// Human-readable recommendation for fixing this issue
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;
}

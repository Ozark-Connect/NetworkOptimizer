using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Issue found with unnecessary tagged VLANs on access ports.
/// </summary>
public class AccessPortVlanIssue
{
    /// <summary>
    /// MAC address of the switch device
    /// </summary>
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the switch device
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Port index number
    /// </summary>
    public int PortIndex { get; set; }

    /// <summary>
    /// Port name (if configured)
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// Network config IDs of tagged VLANs found on this access port
    /// </summary>
    public List<string> TaggedVlanIds { get; set; } = new();

    /// <summary>
    /// Display names of tagged VLANs
    /// </summary>
    public List<string> TaggedVlanNames { get; set; } = new();

    /// <summary>
    /// Device category of connected device (if any)
    /// </summary>
    public ClientDeviceCategory ConnectedDeviceType { get; set; }

    /// <summary>
    /// Severity based on which VLANs are tagged
    /// </summary>
    public DiagnosticSeverity Severity { get; set; }

    /// <summary>
    /// Human-readable recommendation
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;
}

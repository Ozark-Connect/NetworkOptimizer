namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Represents a switch port configuration
/// </summary>
public class PortInfo
{
    /// <summary>
    /// Port index/number
    /// </summary>
    public required int PortIndex { get; init; }

    /// <summary>
    /// Port name/label
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Whether the port link is up
    /// </summary>
    public bool IsUp { get; init; }

    /// <summary>
    /// Link speed in Mbps (e.g., 1000 = 1G)
    /// </summary>
    public int Speed { get; init; }

    /// <summary>
    /// Forward mode (native, all/trunk, disabled, custom)
    /// </summary>
    public string? ForwardMode { get; init; }

    /// <summary>
    /// Whether this is an uplink port
    /// </summary>
    public bool IsUplink { get; init; }

    /// <summary>
    /// Whether this is a WAN port
    /// </summary>
    public bool IsWan { get; init; }

    /// <summary>
    /// Native network ID (for access ports)
    /// </summary>
    public string? NativeNetworkId { get; init; }

    /// <summary>
    /// Excluded network IDs (for trunk ports)
    /// </summary>
    public List<string>? ExcludedNetworkIds { get; init; }

    /// <summary>
    /// Whether port security is enabled
    /// </summary>
    public bool PortSecurityEnabled { get; init; }

    /// <summary>
    /// MAC addresses allowed on this port (MAC filtering)
    /// </summary>
    public List<string>? AllowedMacAddresses { get; init; }

    /// <summary>
    /// Whether port isolation is enabled
    /// </summary>
    public bool IsolationEnabled { get; init; }

    /// <summary>
    /// Whether PoE is enabled on this port
    /// </summary>
    public bool PoeEnabled { get; init; }

    /// <summary>
    /// PoE power draw in watts
    /// </summary>
    public double PoePower { get; init; }

    /// <summary>
    /// PoE mode (auto, off, pasv24, passthrough)
    /// </summary>
    public string? PoeMode { get; init; }

    /// <summary>
    /// Whether this port supports PoE
    /// </summary>
    public bool SupportsPoe { get; init; }

    /// <summary>
    /// The switch this port belongs to
    /// </summary>
    public required SwitchInfo Switch { get; init; }
}

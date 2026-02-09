namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Represents a UniFi switch or gateway device with switch ports
/// </summary>
public class SwitchInfo
{
    /// <summary>
    /// Device name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// MAC address
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>
    /// Model code (e.g., "USW-Enterprise-8-PoE")
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Friendly model name
    /// </summary>
    public string? ModelName { get; init; }

    /// <summary>
    /// Device type (usw, udm, ugw, uxg)
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// IP address
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// Configured DNS server 1 (from config_network.dns1)
    /// </summary>
    public string? ConfiguredDns1 { get; init; }

    /// <summary>
    /// Configured DNS server 2 (from config_network.dns2)
    /// </summary>
    public string? ConfiguredDns2 { get; init; }

    /// <summary>
    /// Network configuration type (dhcp, static)
    /// </summary>
    public string? NetworkConfigType { get; init; }

    /// <summary>
    /// Whether this is a gateway device (UDM, UXG, etc.)
    /// </summary>
    public bool IsGateway { get; init; }

    /// <summary>
    /// Whether this is a UDM-family device acting as an Access Point (mesh AP).
    /// When true, the device should be labeled as [AP] instead of [Switch].
    /// </summary>
    public bool IsAccessPoint { get; init; }

    /// <summary>
    /// Whether this device's ports are unmanageable in UniFi Port Manager.
    /// UX and UX7 devices in AP mode don't expose their switch ports for configuration,
    /// so port-level audit issues are not actionable.
    /// </summary>
    public bool HasUnmanageablePorts =>
        IsAccessPoint && ModelName is "UX" or "UX7";

    /// <summary>
    /// Switch capabilities
    /// </summary>
    public SwitchCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Port table
    /// </summary>
    public List<PortInfo> Ports { get; init; } = new();
}

/// <summary>
/// Switch hardware capabilities
/// </summary>
public class SwitchCapabilities
{
    /// <summary>
    /// Maximum number of custom MAC ACLs supported
    /// </summary>
    public int MaxCustomMacAcls { get; init; }

    /// <summary>
    /// Whether the switch supports port isolation
    /// </summary>
    public bool SupportsIsolation { get; init; }

    /// <summary>
    /// Whether the switch supports PoE
    /// </summary>
    public bool SupportsPoe { get; init; }

    /// <summary>
    /// Maximum PoE power budget in watts
    /// </summary>
    public double MaxPoePower { get; init; }
}

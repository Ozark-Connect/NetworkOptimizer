namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Network classification types based on purpose
/// </summary>
public enum NetworkPurpose
{
    /// <summary>
    /// Corporate/business network for general use
    /// </summary>
    Corporate,

    /// <summary>
    /// Home/primary residential network
    /// </summary>
    Home,

    /// <summary>
    /// IoT devices (smart home, automation, etc.)
    /// </summary>
    IoT,

    /// <summary>
    /// Security cameras and surveillance equipment
    /// </summary>
    Security,

    /// <summary>
    /// Guest network for visitors
    /// </summary>
    Guest,

    /// <summary>
    /// Management/admin network for infrastructure
    /// </summary>
    Management,

    /// <summary>
    /// Unknown or unclassified network
    /// </summary>
    Unknown
}

/// <summary>
/// Extension methods for NetworkPurpose
/// </summary>
public static class NetworkPurposeExtensions
{
    /// <summary>
    /// Get a human-friendly display name for the purpose
    /// </summary>
    public static string ToDisplayString(this NetworkPurpose purpose) => purpose switch
    {
        NetworkPurpose.Corporate => "Corporate",
        NetworkPurpose.Home => "Home",
        NetworkPurpose.IoT => "IoT",
        NetworkPurpose.Security => "Security",
        NetworkPurpose.Guest => "Guest",
        NetworkPurpose.Management => "Management",
        NetworkPurpose.Unknown => "Unclassified",
        _ => purpose.ToString()
    };
}

/// <summary>
/// Represents a network/VLAN configuration
/// </summary>
public class NetworkInfo
{
    /// <summary>
    /// Network ID (from UniFi)
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Network name
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// VLAN ID (1 for native/untagged)
    /// </summary>
    public required int VlanId { get; init; }

    /// <summary>
    /// Purpose/classification of the network
    /// </summary>
    public NetworkPurpose Purpose { get; init; }

    /// <summary>
    /// IP subnet (e.g., "192.168.1.0/24")
    /// </summary>
    public string? Subnet { get; init; }

    /// <summary>
    /// Gateway IP address
    /// </summary>
    public string? Gateway { get; init; }

    /// <summary>
    /// DNS servers for this network
    /// </summary>
    public List<string>? DnsServers { get; init; }

    /// <summary>
    /// Whether this is the native/default VLAN
    /// </summary>
    public bool IsNative => VlanId == 1;

    /// <summary>
    /// Whether inter-VLAN routing is enabled
    /// </summary>
    public bool AllowsRouting { get; init; }

    /// <summary>
    /// Whether DHCP server is enabled on this network
    /// </summary>
    public bool DhcpEnabled { get; init; }

    /// <summary>
    /// Whether network isolation is enabled (blocks inter-VLAN traffic by default)
    /// </summary>
    public bool NetworkIsolationEnabled { get; init; }

    /// <summary>
    /// Whether internet access is enabled for this network
    /// </summary>
    public bool InternetAccessEnabled { get; init; }
}

namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Network classification types based on purpose.
/// Note: Corporate is the default (value 0), matching UniFi's API behavior.
/// </summary>
public enum NetworkPurpose
{
    /// <summary>
    /// Corporate/business network for general use (default)
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
    /// Printer/scanner network
    /// </summary>
    Printer,

    /// <summary>
    /// DMZ (demilitarized zone) network - isolated with internet access but restricted LAN access
    /// </summary>
    Dmz,

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
        NetworkPurpose.Printer => "Printer",
        NetworkPurpose.Dmz => "DMZ",
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

    /// <summary>
    /// Whether this is an official UniFi Guest network (purpose="guest" in API).
    /// These networks have implicit isolation at the switch/AP level that doesn't
    /// appear in firewall rules, so we skip firewall isolation checks for them.
    /// </summary>
    public bool IsUniFiGuestNetwork { get; init; }

    /// <summary>
    /// Firewall zone ID for this network (from UniFi).
    /// LAN networks share one zone ID, WAN networks have a different zone ID.
    /// Used to detect firewall rules that block internet access.
    /// </summary>
    public string? FirewallZoneId { get; init; }

    /// <summary>
    /// Network group (LAN, WAN, WAN2, etc.).
    /// Used to identify whether this is an internal or external network.
    /// </summary>
    public string? NetworkGroup { get; init; }

    /// <summary>
    /// Whether UPnP is enabled for this specific network.
    /// When true, devices on this network can use UPnP to open ports on the firewall.
    /// </summary>
    public bool UpnpLanEnabled { get; init; }

    /// <summary>
    /// Whether this network is enabled/active.
    /// Disabled networks are configured but not active on the gateway.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

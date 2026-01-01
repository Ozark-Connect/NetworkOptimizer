using System.Net;

namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Represents the complete network configuration including VLANs, firewall rules, and port configurations.
/// </summary>
public class NetworkConfiguration
{
    /// <summary>
    /// Unique identifier for the network configuration.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Site identifier in the UniFi controller.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Site name.
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the configuration was captured.
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// VLAN configurations.
    /// </summary>
    public List<VlanConfiguration> Vlans { get; set; } = new();

    /// <summary>
    /// Firewall rule configurations.
    /// </summary>
    public List<FirewallRule> FirewallRules { get; set; } = new();

    /// <summary>
    /// Port configuration for switches.
    /// </summary>
    public List<PortConfiguration> PortConfigurations { get; set; } = new();

    /// <summary>
    /// Wireless network configurations.
    /// </summary>
    public List<WirelessNetwork> WirelessNetworks { get; set; } = new();

    /// <summary>
    /// DHCP server configurations.
    /// </summary>
    public List<DhcpConfiguration> DhcpConfigurations { get; set; } = new();

    /// <summary>
    /// Static route configurations.
    /// </summary>
    public List<StaticRoute> StaticRoutes { get; set; } = new();

    /// <summary>
    /// Additional metadata for the configuration.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Represents a VLAN configuration.
/// </summary>
public class VlanConfiguration
{
    /// <summary>
    /// VLAN ID (1-4094).
    /// </summary>
    public int VlanId { get; set; }

    /// <summary>
    /// VLAN name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// VLAN purpose or description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Network subnet (CIDR notation, e.g., "192.168.1.0/24").
    /// </summary>
    public string Subnet { get; set; } = string.Empty;

    /// <summary>
    /// Gateway IP address for the VLAN.
    /// </summary>
    public IPAddress? GatewayIp { get; set; }

    /// <summary>
    /// DHCP enabled status.
    /// </summary>
    public bool DhcpEnabled { get; set; }

    /// <summary>
    /// DHCP range start address.
    /// </summary>
    public IPAddress? DhcpRangeStart { get; set; }

    /// <summary>
    /// DHCP range end address.
    /// </summary>
    public IPAddress? DhcpRangeEnd { get; set; }

    /// <summary>
    /// DNS servers for the VLAN.
    /// </summary>
    public List<IPAddress> DnsServers { get; set; } = new();

    /// <summary>
    /// Domain name for the VLAN.
    /// </summary>
    public string? DomainName { get; set; }

    /// <summary>
    /// Indicates whether inter-VLAN routing is enabled.
    /// </summary>
    public bool InterVlanRoutingEnabled { get; set; }

    /// <summary>
    /// Indicates whether the VLAN is isolated (guest network).
    /// </summary>
    public bool IsIsolated { get; set; }
}

/// <summary>
/// Represents a firewall rule configuration.
/// </summary>
public class FirewallRule
{
    /// <summary>
    /// Rule identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Rule name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Rule description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Rule action (Accept, Drop, Reject).
    /// </summary>
    public FirewallAction Action { get; set; } = FirewallAction.Accept;

    /// <summary>
    /// Rule direction (In, Out, Local).
    /// </summary>
    public FirewallDirection Direction { get; set; } = FirewallDirection.In;

    /// <summary>
    /// Protocol (TCP, UDP, ICMP, All).
    /// </summary>
    public string Protocol { get; set; } = "all";

    /// <summary>
    /// Source address or network.
    /// </summary>
    public string? SourceAddress { get; set; }

    /// <summary>
    /// Source port or port range.
    /// </summary>
    public string? SourcePort { get; set; }

    /// <summary>
    /// Destination address or network.
    /// </summary>
    public string? DestinationAddress { get; set; }

    /// <summary>
    /// Destination port or port range.
    /// </summary>
    public string? DestinationPort { get; set; }

    /// <summary>
    /// Rule priority/order.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Indicates whether the rule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Indicates whether logging is enabled for this rule.
    /// </summary>
    public bool LoggingEnabled { get; set; }

    /// <summary>
    /// Timestamp when the rule was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the rule was last modified.
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Firewall rule action types.
/// </summary>
public enum FirewallAction
{
    Accept,
    Drop,
    Reject
}

/// <summary>
/// Firewall rule direction types.
/// </summary>
public enum FirewallDirection
{
    In,
    Out,
    Local
}

/// <summary>
/// Represents a switch port configuration.
/// </summary>
public class PortConfiguration
{
    /// <summary>
    /// Device identifier where the port is located.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Port number.
    /// </summary>
    public int PortNumber { get; set; }

    /// <summary>
    /// Port name or label.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Port profile name.
    /// </summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Native VLAN ID for the port.
    /// </summary>
    public int NativeVlanId { get; set; } = 1;

    /// <summary>
    /// Tagged VLAN IDs (for trunk ports).
    /// </summary>
    public List<int> TaggedVlans { get; set; } = new();

    /// <summary>
    /// Port mode (Access, Trunk).
    /// </summary>
    public PortMode Mode { get; set; } = PortMode.Access;

    /// <summary>
    /// PoE mode (Off, Auto, Passive24V, etc.).
    /// </summary>
    public string PoeMode { get; set; } = "off";

    /// <summary>
    /// Port speed (10, 100, 1000, 10000, auto).
    /// </summary>
    public string Speed { get; set; } = "auto";

    /// <summary>
    /// Full duplex mode enabled.
    /// </summary>
    public bool FullDuplex { get; set; } = true;

    /// <summary>
    /// STP (Spanning Tree Protocol) enabled.
    /// </summary>
    public bool StpEnabled { get; set; } = true;

    /// <summary>
    /// Storm control enabled.
    /// </summary>
    public bool StormControlEnabled { get; set; }

    /// <summary>
    /// Port isolation enabled.
    /// </summary>
    public bool IsolationEnabled { get; set; }

    /// <summary>
    /// Indicates whether the port is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Port mode types.
/// </summary>
public enum PortMode
{
    Access,
    Trunk
}

/// <summary>
/// Represents a wireless network configuration.
/// </summary>
public class WirelessNetwork
{
    /// <summary>
    /// Network identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// SSID (network name).
    /// </summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>
    /// Security type (Open, WPA2, WPA3, etc.).
    /// </summary>
    public string SecurityType { get; set; } = string.Empty;

    /// <summary>
    /// VLAN ID for the wireless network.
    /// </summary>
    public int VlanId { get; set; }

    /// <summary>
    /// Indicates whether the network is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Indicates whether the SSID is hidden.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// Indicates whether this is a guest network.
    /// </summary>
    public bool IsGuest { get; set; }

    /// <summary>
    /// Guest portal settings (if applicable).
    /// </summary>
    public GuestPortalSettings? GuestPortal { get; set; }

    /// <summary>
    /// Band steering enabled (2.4GHz/5GHz).
    /// </summary>
    public bool BandSteeringEnabled { get; set; }

    /// <summary>
    /// Minimum RSSI for client connections.
    /// </summary>
    public int? MinimumRssi { get; set; }

    /// <summary>
    /// Fast roaming enabled (802.11r).
    /// </summary>
    public bool FastRoamingEnabled { get; set; }
}

/// <summary>
/// Guest portal settings for wireless networks.
/// </summary>
public class GuestPortalSettings
{
    /// <summary>
    /// Portal enabled status.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Portal authentication type.
    /// </summary>
    public string AuthenticationType { get; set; } = string.Empty;

    /// <summary>
    /// Session timeout in minutes.
    /// </summary>
    public int SessionTimeoutMinutes { get; set; }

    /// <summary>
    /// Download speed limit in Mbps.
    /// </summary>
    public int? DownloadLimitMbps { get; set; }

    /// <summary>
    /// Upload speed limit in Mbps.
    /// </summary>
    public int? UploadLimitMbps { get; set; }
}

/// <summary>
/// Represents a DHCP server configuration.
/// </summary>
public class DhcpConfiguration
{
    /// <summary>
    /// VLAN ID where DHCP is configured.
    /// </summary>
    public int VlanId { get; set; }

    /// <summary>
    /// DHCP enabled status.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Lease time in seconds.
    /// </summary>
    public int LeaseTimeSeconds { get; set; } = 86400;

    /// <summary>
    /// Static DHCP reservations.
    /// </summary>
    public List<DhcpReservation> Reservations { get; set; } = new();
}

/// <summary>
/// Represents a static DHCP reservation.
/// </summary>
public class DhcpReservation
{
    /// <summary>
    /// MAC address.
    /// </summary>
    public string MacAddress { get; set; } = string.Empty;

    /// <summary>
    /// Reserved IP address.
    /// </summary>
    public IPAddress IpAddress { get; set; } = IPAddress.None;

    /// <summary>
    /// Hostname.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Description or note.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Represents a static route configuration.
/// </summary>
public class StaticRoute
{
    /// <summary>
    /// Destination network (CIDR notation).
    /// </summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// Gateway IP address.
    /// </summary>
    public IPAddress Gateway { get; set; } = IPAddress.None;

    /// <summary>
    /// Metric/priority for the route.
    /// </summary>
    public int Metric { get; set; }

    /// <summary>
    /// Indicates whether the route is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Route description.
    /// </summary>
    public string? Description { get; set; }
}

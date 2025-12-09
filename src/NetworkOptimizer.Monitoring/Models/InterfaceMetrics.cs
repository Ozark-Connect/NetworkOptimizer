namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Represents network interface metrics collected via SNMP
/// </summary>
public class InterfaceMetrics
{
    /// <summary>
    /// Interface index (ifIndex)
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Interface description (ifDescr)
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Interface name/alias (ifAlias)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Interface type (ifType)
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Interface speed in bits per second (ifSpeed)
    /// </summary>
    public long Speed { get; set; }

    /// <summary>
    /// High-capacity interface speed in Mbps (ifHighSpeed) - for 10G+ interfaces
    /// </summary>
    public long HighSpeed { get; set; }

    /// <summary>
    /// Physical address (MAC) (ifPhysAddress)
    /// </summary>
    public string PhysicalAddress { get; set; } = string.Empty;

    /// <summary>
    /// Administrative status (ifAdminStatus): 1=up, 2=down, 3=testing
    /// </summary>
    public int AdminStatus { get; set; }

    /// <summary>
    /// Operational status (ifOperStatus): 1=up, 2=down, 3=testing, 4=unknown, 5=dormant, 6=notPresent, 7=lowerLayerDown
    /// </summary>
    public int OperStatus { get; set; }

    /// <summary>
    /// Last change time in hundredths of a second (ifLastChange)
    /// </summary>
    public long LastChange { get; set; }

    /// <summary>
    /// Total octets received (ifInOctets or ifHCInOctets for 64-bit)
    /// </summary>
    public long InOctets { get; set; }

    /// <summary>
    /// Total unicast packets received (ifInUcastPkts or ifHCInUcastPkts for 64-bit)
    /// </summary>
    public long InUcastPkts { get; set; }

    /// <summary>
    /// Total multicast packets received (ifInMulticastPkts or ifHCInMulticastPkts for 64-bit)
    /// </summary>
    public long InMulticastPkts { get; set; }

    /// <summary>
    /// Total broadcast packets received (ifInBroadcastPkts or ifHCInBroadcastPkts for 64-bit)
    /// </summary>
    public long InBroadcastPkts { get; set; }

    /// <summary>
    /// Inbound packets discarded (ifInDiscards)
    /// </summary>
    public long InDiscards { get; set; }

    /// <summary>
    /// Inbound packets with errors (ifInErrors)
    /// </summary>
    public long InErrors { get; set; }

    /// <summary>
    /// Inbound packets with unknown protocols (ifInUnknownProtos)
    /// </summary>
    public long InUnknownProtos { get; set; }

    /// <summary>
    /// Total octets transmitted (ifOutOctets or ifHCOutOctets for 64-bit)
    /// </summary>
    public long OutOctets { get; set; }

    /// <summary>
    /// Total unicast packets transmitted (ifOutUcastPkts or ifHCOutUcastPkts for 64-bit)
    /// </summary>
    public long OutUcastPkts { get; set; }

    /// <summary>
    /// Total multicast packets transmitted (ifOutMulticastPkts or ifHCOutMulticastPkts for 64-bit)
    /// </summary>
    public long OutMulticastPkts { get; set; }

    /// <summary>
    /// Total broadcast packets transmitted (ifOutBroadcastPkts or ifHCOutBroadcastPkts for 64-bit)
    /// </summary>
    public long OutBroadcastPkts { get; set; }

    /// <summary>
    /// Outbound packets discarded (ifOutDiscards)
    /// </summary>
    public long OutDiscards { get; set; }

    /// <summary>
    /// Outbound packets with errors (ifOutErrors)
    /// </summary>
    public long OutErrors { get; set; }

    /// <summary>
    /// MTU size in octets (ifMtu)
    /// </summary>
    public int Mtu { get; set; }

    /// <summary>
    /// Timestamp when metrics were collected
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Device IP address from which metrics were collected
    /// </summary>
    public string DeviceIp { get; set; } = string.Empty;

    /// <summary>
    /// Device hostname
    /// </summary>
    public string DeviceHostname { get; set; } = string.Empty;

    /// <summary>
    /// Whether the interface is operationally up
    /// </summary>
    public bool IsUp => OperStatus == 1;

    /// <summary>
    /// Whether the interface is administratively enabled
    /// </summary>
    public bool IsEnabled => AdminStatus == 1;

    /// <summary>
    /// Interface speed in Mbps (calculated)
    /// </summary>
    public double SpeedMbps => HighSpeed > 0 ? HighSpeed : Speed / 1_000_000.0;

    /// <summary>
    /// Interface speed in Gbps (calculated)
    /// </summary>
    public double SpeedGbps => SpeedMbps / 1_000.0;

    /// <summary>
    /// Total inbound packets
    /// </summary>
    public long TotalInPackets => InUcastPkts + InMulticastPkts + InBroadcastPkts;

    /// <summary>
    /// Total outbound packets
    /// </summary>
    public long TotalOutPackets => OutUcastPkts + OutMulticastPkts + OutBroadcastPkts;

    /// <summary>
    /// Total inbound errors and discards
    /// </summary>
    public long TotalInProblems => InErrors + InDiscards;

    /// <summary>
    /// Total outbound errors and discards
    /// </summary>
    public long TotalOutProblems => OutErrors + OutDiscards;

    /// <summary>
    /// Whether this interface should be monitored (excludes virtual/internal interfaces)
    /// </summary>
    public bool ShouldMonitor()
    {
        var desc = Description.ToLowerInvariant();
        var name = Name.ToLowerInvariant();

        // Exclude common virtual/internal interfaces
        var excludePatterns = new[]
        {
            "lo",        // Loopback
            "br-",       // Bridge
            "docker",    // Docker
            "veth",      // Virtual Ethernet
            "ifb",       // Intermediate Functional Block
            "virbr",     // Virtual Bridge
            "tun",       // Tunnel
            "tap",       // TAP device
            "null"       // Null interface
        };

        foreach (var pattern in excludePatterns)
        {
            if (desc.StartsWith(pattern) || name.StartsWith(pattern))
                return false;
        }

        return true;
    }
}

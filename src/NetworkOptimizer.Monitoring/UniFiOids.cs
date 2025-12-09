namespace NetworkOptimizer.Monitoring;

/// <summary>
/// SNMP Object Identifiers (OIDs) for standard MIB-II and UniFi-specific metrics
/// </summary>
public static class UniFiOids
{
    #region System Group (MIB-II)

    /// <summary>
    /// System description - sysDescr (.1.3.6.1.2.1.1.1.0)
    /// </summary>
    public const string SysDescr = "1.3.6.1.2.1.1.1.0";

    /// <summary>
    /// System object identifier - sysObjectID (.1.3.6.1.2.1.1.2.0)
    /// </summary>
    public const string SysObjectID = "1.3.6.1.2.1.1.2.0";

    /// <summary>
    /// System uptime in hundredths of a second - sysUpTime (.1.3.6.1.2.1.1.3.0)
    /// </summary>
    public const string SysUpTime = "1.3.6.1.2.1.1.3.0";

    /// <summary>
    /// System contact - sysContact (.1.3.6.1.2.1.1.4.0)
    /// </summary>
    public const string SysContact = "1.3.6.1.2.1.1.4.0";

    /// <summary>
    /// System name/hostname - sysName (.1.3.6.1.2.1.1.5.0)
    /// </summary>
    public const string SysName = "1.3.6.1.2.1.1.5.0";

    /// <summary>
    /// System location - sysLocation (.1.3.6.1.2.1.1.6.0)
    /// </summary>
    public const string SysLocation = "1.3.6.1.2.1.1.6.0";

    /// <summary>
    /// System services - sysServices (.1.3.6.1.2.1.1.7.0)
    /// </summary>
    public const string SysServices = "1.3.6.1.2.1.1.7.0";

    #endregion

    #region Interface Group (MIB-II)

    /// <summary>
    /// Number of network interfaces - ifNumber (.1.3.6.1.2.1.2.1.0)
    /// </summary>
    public const string IfNumber = "1.3.6.1.2.1.2.1.0";

    /// <summary>
    /// Interface table - ifTable (.1.3.6.1.2.1.2.2)
    /// </summary>
    public const string IfTable = "1.3.6.1.2.1.2.2";

    /// <summary>
    /// Interface index - ifIndex (.1.3.6.1.2.1.2.2.1.1)
    /// </summary>
    public const string IfIndex = "1.3.6.1.2.1.2.2.1.1";

    /// <summary>
    /// Interface description - ifDescr (.1.3.6.1.2.1.2.2.1.2)
    /// </summary>
    public const string IfDescr = "1.3.6.1.2.1.2.2.1.2";

    /// <summary>
    /// Interface type - ifType (.1.3.6.1.2.1.2.2.1.3)
    /// </summary>
    public const string IfType = "1.3.6.1.2.1.2.2.1.3";

    /// <summary>
    /// Interface MTU - ifMtu (.1.3.6.1.2.1.2.2.1.4)
    /// </summary>
    public const string IfMtu = "1.3.6.1.2.1.2.2.1.4";

    /// <summary>
    /// Interface speed in bits per second - ifSpeed (.1.3.6.1.2.1.2.2.1.5)
    /// </summary>
    public const string IfSpeed = "1.3.6.1.2.1.2.2.1.5";

    /// <summary>
    /// Interface physical address (MAC) - ifPhysAddress (.1.3.6.1.2.1.2.2.1.6)
    /// </summary>
    public const string IfPhysAddress = "1.3.6.1.2.1.2.2.1.6";

    /// <summary>
    /// Interface administrative status - ifAdminStatus (.1.3.6.1.2.1.2.2.1.7)
    /// </summary>
    public const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";

    /// <summary>
    /// Interface operational status - ifOperStatus (.1.3.6.1.2.1.2.2.1.8)
    /// </summary>
    public const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";

    /// <summary>
    /// Interface last change - ifLastChange (.1.3.6.1.2.1.2.2.1.9)
    /// </summary>
    public const string IfLastChange = "1.3.6.1.2.1.2.2.1.9";

    /// <summary>
    /// Interface inbound octets - ifInOctets (.1.3.6.1.2.1.2.2.1.10)
    /// </summary>
    public const string IfInOctets = "1.3.6.1.2.1.2.2.1.10";

    /// <summary>
    /// Interface inbound unicast packets - ifInUcastPkts (.1.3.6.1.2.1.2.2.1.11)
    /// </summary>
    public const string IfInUcastPkts = "1.3.6.1.2.1.2.2.1.11";

    /// <summary>
    /// Interface inbound non-unicast packets - ifInNUcastPkts (.1.3.6.1.2.1.2.2.1.12)
    /// </summary>
    public const string IfInNUcastPkts = "1.3.6.1.2.1.2.2.1.12";

    /// <summary>
    /// Interface inbound discarded packets - ifInDiscards (.1.3.6.1.2.1.2.2.1.13)
    /// </summary>
    public const string IfInDiscards = "1.3.6.1.2.1.2.2.1.13";

    /// <summary>
    /// Interface inbound errors - ifInErrors (.1.3.6.1.2.1.2.2.1.14)
    /// </summary>
    public const string IfInErrors = "1.3.6.1.2.1.2.2.1.14";

    /// <summary>
    /// Interface inbound unknown protocol packets - ifInUnknownProtos (.1.3.6.1.2.1.2.2.1.15)
    /// </summary>
    public const string IfInUnknownProtos = "1.3.6.1.2.1.2.2.1.15";

    /// <summary>
    /// Interface outbound octets - ifOutOctets (.1.3.6.1.2.1.2.2.1.16)
    /// </summary>
    public const string IfOutOctets = "1.3.6.1.2.1.2.2.1.16";

    /// <summary>
    /// Interface outbound unicast packets - ifOutUcastPkts (.1.3.6.1.2.1.2.2.1.17)
    /// </summary>
    public const string IfOutUcastPkts = "1.3.6.1.2.1.2.2.1.17";

    /// <summary>
    /// Interface outbound non-unicast packets - ifOutNUcastPkts (.1.3.6.1.2.1.2.2.1.18)
    /// </summary>
    public const string IfOutNUcastPkts = "1.3.6.1.2.1.2.2.1.18";

    /// <summary>
    /// Interface outbound discarded packets - ifOutDiscards (.1.3.6.1.2.1.2.2.1.19)
    /// </summary>
    public const string IfOutDiscards = "1.3.6.1.2.1.2.2.1.19";

    /// <summary>
    /// Interface outbound errors - ifOutErrors (.1.3.6.1.2.1.2.2.1.20)
    /// </summary>
    public const string IfOutErrors = "1.3.6.1.2.1.2.2.1.20";

    /// <summary>
    /// Interface outbound queue length - ifOutQLen (.1.3.6.1.2.1.2.2.1.21)
    /// </summary>
    public const string IfOutQLen = "1.3.6.1.2.1.2.2.1.21";

    /// <summary>
    /// Interface specific - ifSpecific (.1.3.6.1.2.1.2.2.1.22)
    /// </summary>
    public const string IfSpecific = "1.3.6.1.2.1.2.2.1.22";

    #endregion

    #region Interface Extensions (MIB-II ifXTable)

    /// <summary>
    /// Interface extensions table - ifXTable (.1.3.6.1.2.1.31.1.1)
    /// </summary>
    public const string IfXTable = "1.3.6.1.2.1.31.1.1";

    /// <summary>
    /// Interface name/alias - ifName (.1.3.6.1.2.1.31.1.1.1.1)
    /// </summary>
    public const string IfName = "1.3.6.1.2.1.31.1.1.1.1";

    /// <summary>
    /// Interface inbound multicast packets - ifInMulticastPkts (.1.3.6.1.2.1.31.1.1.1.2)
    /// </summary>
    public const string IfInMulticastPkts = "1.3.6.1.2.1.31.1.1.1.2";

    /// <summary>
    /// Interface inbound broadcast packets - ifInBroadcastPkts (.1.3.6.1.2.1.31.1.1.1.3)
    /// </summary>
    public const string IfInBroadcastPkts = "1.3.6.1.2.1.31.1.1.1.3";

    /// <summary>
    /// Interface outbound multicast packets - ifOutMulticastPkts (.1.3.6.1.2.1.31.1.1.1.4)
    /// </summary>
    public const string IfOutMulticastPkts = "1.3.6.1.2.1.31.1.1.1.4";

    /// <summary>
    /// Interface outbound broadcast packets - ifOutBroadcastPkts (.1.3.6.1.2.1.31.1.1.1.5)
    /// </summary>
    public const string IfOutBroadcastPkts = "1.3.6.1.2.1.31.1.1.1.5";

    /// <summary>
    /// High-capacity inbound octets (64-bit) - ifHCInOctets (.1.3.6.1.2.1.31.1.1.1.6)
    /// </summary>
    public const string IfHCInOctets = "1.3.6.1.2.1.31.1.1.1.6";

    /// <summary>
    /// High-capacity inbound unicast packets (64-bit) - ifHCInUcastPkts (.1.3.6.1.2.1.31.1.1.1.7)
    /// </summary>
    public const string IfHCInUcastPkts = "1.3.6.1.2.1.31.1.1.1.7";

    /// <summary>
    /// High-capacity inbound multicast packets (64-bit) - ifHCInMulticastPkts (.1.3.6.1.2.1.31.1.1.1.8)
    /// </summary>
    public const string IfHCInMulticastPkts = "1.3.6.1.2.1.31.1.1.1.8";

    /// <summary>
    /// High-capacity inbound broadcast packets (64-bit) - ifHCInBroadcastPkts (.1.3.6.1.2.1.31.1.1.1.9)
    /// </summary>
    public const string IfHCInBroadcastPkts = "1.3.6.1.2.1.31.1.1.1.9";

    /// <summary>
    /// High-capacity outbound octets (64-bit) - ifHCOutOctets (.1.3.6.1.2.1.31.1.1.1.10)
    /// </summary>
    public const string IfHCOutOctets = "1.3.6.1.2.1.31.1.1.1.10";

    /// <summary>
    /// High-capacity outbound unicast packets (64-bit) - ifHCOutUcastPkts (.1.3.6.1.2.1.31.1.1.1.11)
    /// </summary>
    public const string IfHCOutUcastPkts = "1.3.6.1.2.1.31.1.1.1.11";

    /// <summary>
    /// High-capacity outbound multicast packets (64-bit) - ifHCOutMulticastPkts (.1.3.6.1.2.1.31.1.1.1.12)
    /// </summary>
    public const string IfHCOutMulticastPkts = "1.3.6.1.2.1.31.1.1.1.12";

    /// <summary>
    /// High-capacity outbound broadcast packets (64-bit) - ifHCOutBroadcastPkts (.1.3.6.1.2.1.31.1.1.1.13)
    /// </summary>
    public const string IfHCOutBroadcastPkts = "1.3.6.1.2.1.31.1.1.1.13";

    /// <summary>
    /// Interface link up/down trap enable - ifLinkUpDownTrapEnable (.1.3.6.1.2.1.31.1.1.1.14)
    /// </summary>
    public const string IfLinkUpDownTrapEnable = "1.3.6.1.2.1.31.1.1.1.14";

    /// <summary>
    /// Interface high speed in Mbps - ifHighSpeed (.1.3.6.1.2.1.31.1.1.1.15)
    /// </summary>
    public const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";

    /// <summary>
    /// Interface promiscuous mode - ifPromiscuousMode (.1.3.6.1.2.1.31.1.1.1.16)
    /// </summary>
    public const string IfPromiscuousMode = "1.3.6.1.2.1.31.1.1.1.16";

    /// <summary>
    /// Interface connector present - ifConnectorPresent (.1.3.6.1.2.1.31.1.1.1.17)
    /// </summary>
    public const string IfConnectorPresent = "1.3.6.1.2.1.31.1.1.1.17";

    /// <summary>
    /// Interface alias - ifAlias (.1.3.6.1.2.1.31.1.1.1.18)
    /// </summary>
    public const string IfAlias = "1.3.6.1.2.1.31.1.1.1.18";

    #endregion

    #region Host Resources MIB (CPU, Memory)

    /// <summary>
    /// Host resources storage table - hrStorageTable (.1.3.6.1.2.1.25.2.3)
    /// </summary>
    public const string HrStorageTable = "1.3.6.1.2.1.25.2.3";

    /// <summary>
    /// Storage type - hrStorageType (.1.3.6.1.2.1.25.2.3.1.2)
    /// </summary>
    public const string HrStorageType = "1.3.6.1.2.1.25.2.3.1.2";

    /// <summary>
    /// Storage description - hrStorageDescr (.1.3.6.1.2.1.25.2.3.1.3)
    /// </summary>
    public const string HrStorageDescr = "1.3.6.1.2.1.25.2.3.1.3";

    /// <summary>
    /// Storage allocation units - hrStorageAllocationUnits (.1.3.6.1.2.1.25.2.3.1.4)
    /// </summary>
    public const string HrStorageAllocationUnits = "1.3.6.1.2.1.25.2.3.1.4";

    /// <summary>
    /// Storage size - hrStorageSize (.1.3.6.1.2.1.25.2.3.1.5)
    /// </summary>
    public const string HrStorageSize = "1.3.6.1.2.1.25.2.3.1.5";

    /// <summary>
    /// Storage used - hrStorageUsed (.1.3.6.1.2.1.25.2.3.1.6)
    /// </summary>
    public const string HrStorageUsed = "1.3.6.1.2.1.25.2.3.1.6";

    /// <summary>
    /// Processor table - hrProcessorTable (.1.3.6.1.2.1.25.3.3)
    /// </summary>
    public const string HrProcessorTable = "1.3.6.1.2.1.25.3.3";

    /// <summary>
    /// Processor load - hrProcessorLoad (.1.3.6.1.2.1.25.3.3.1.2)
    /// </summary>
    public const string HrProcessorLoad = "1.3.6.1.2.1.25.3.3.1.2";

    #endregion

    #region UCD-SNMP MIB (Alternative CPU/Memory)

    /// <summary>
    /// System stats table - systemStats (.1.3.6.1.4.1.2021.11)
    /// </summary>
    public const string SystemStats = "1.3.6.1.4.1.2021.11";

    /// <summary>
    /// CPU user percentage - ssCpuUser (.1.3.6.1.4.1.2021.11.9.0)
    /// </summary>
    public const string SsCpuUser = "1.3.6.1.4.1.2021.11.9.0";

    /// <summary>
    /// CPU system percentage - ssCpuSystem (.1.3.6.1.4.1.2021.11.10.0)
    /// </summary>
    public const string SsCpuSystem = "1.3.6.1.4.1.2021.11.10.0";

    /// <summary>
    /// CPU idle percentage - ssCpuIdle (.1.3.6.1.4.1.2021.11.11.0)
    /// </summary>
    public const string SsCpuIdle = "1.3.6.1.4.1.2021.11.11.0";

    /// <summary>
    /// Memory table - memTable (.1.3.6.1.4.1.2021.4)
    /// </summary>
    public const string MemTable = "1.3.6.1.4.1.2021.4";

    /// <summary>
    /// Total memory - memTotalReal (.1.3.6.1.4.1.2021.4.5.0)
    /// </summary>
    public const string MemTotalReal = "1.3.6.1.4.1.2021.4.5.0";

    /// <summary>
    /// Available memory - memAvailReal (.1.3.6.1.4.1.2021.4.6.0)
    /// </summary>
    public const string MemAvailReal = "1.3.6.1.4.1.2021.4.6.0";

    /// <summary>
    /// Total swap - memTotalSwap (.1.3.6.1.4.1.2021.4.3.0)
    /// </summary>
    public const string MemTotalSwap = "1.3.6.1.4.1.2021.4.3.0";

    /// <summary>
    /// Available swap - memAvailSwap (.1.3.6.1.4.1.2021.4.4.0)
    /// </summary>
    public const string MemAvailSwap = "1.3.6.1.4.1.2021.4.4.0";

    #endregion

    #region UniFi-Specific OIDs (Ubiquiti Enterprise MIB)

    /// <summary>
    /// Ubiquiti enterprise base OID - .1.3.6.1.4.1.41112
    /// </summary>
    public const string UbiquitiBase = "1.3.6.1.4.1.41112";

    /// <summary>
    /// UniFi device model - .1.3.6.1.4.1.41112.1.6.3.3.0
    /// </summary>
    public const string UniFiModel = "1.3.6.1.4.1.41112.1.6.3.3.0";

    /// <summary>
    /// UniFi firmware version - .1.3.6.1.4.1.41112.1.6.3.6.0
    /// </summary>
    public const string UniFiFirmwareVersion = "1.3.6.1.4.1.41112.1.6.3.6.0";

    /// <summary>
    /// UniFi device MAC address - .1.3.6.1.4.1.41112.1.6.3.5.0
    /// </summary>
    public const string UniFiMacAddress = "1.3.6.1.4.1.41112.1.6.3.5.0";

    /// <summary>
    /// UniFi device temperature (if available) - varies by device
    /// </summary>
    public const string UniFiTemperature = "1.3.6.1.4.1.41112.1.6.1.2.1.5";

    #endregion

    #region Entity MIB (Physical sensors)

    /// <summary>
    /// Entity physical table - entPhysicalTable (.1.3.6.1.2.1.47.1.1.1)
    /// </summary>
    public const string EntPhysicalTable = "1.3.6.1.2.1.47.1.1.1";

    /// <summary>
    /// Entity sensor table - entPhySensorTable (.1.3.6.1.2.1.99.1.1)
    /// </summary>
    public const string EntPhySensorTable = "1.3.6.1.2.1.99.1.1";

    /// <summary>
    /// Entity sensor value - entPhySensorValue (.1.3.6.1.2.1.99.1.1.1.4)
    /// </summary>
    public const string EntPhySensorValue = "1.3.6.1.2.1.99.1.1.1.4";

    #endregion

    #region IP Group (MIB-II)

    /// <summary>
    /// IP forwarding enabled - ipForwarding (.1.3.6.1.2.1.4.1.0)
    /// </summary>
    public const string IpForwarding = "1.3.6.1.2.1.4.1.0";

    /// <summary>
    /// IP default TTL - ipDefaultTTL (.1.3.6.1.2.1.4.2.0)
    /// </summary>
    public const string IpDefaultTTL = "1.3.6.1.2.1.4.2.0";

    /// <summary>
    /// IP address table - ipAddrTable (.1.3.6.1.2.1.4.20)
    /// </summary>
    public const string IpAddrTable = "1.3.6.1.2.1.4.20";

    #endregion

    #region SNMP Group (MIB-II)

    /// <summary>
    /// SNMP in packets - snmpInPkts (.1.3.6.1.2.1.11.1.0)
    /// </summary>
    public const string SnmpInPkts = "1.3.6.1.2.1.11.1.0";

    /// <summary>
    /// SNMP out packets - snmpOutPkts (.1.3.6.1.2.1.11.2.0)
    /// </summary>
    public const string SnmpOutPkts = "1.3.6.1.2.1.11.2.0";

    #endregion

    /// <summary>
    /// Get OID for a specific interface index
    /// </summary>
    public static string GetInterfaceOid(string baseOid, int interfaceIndex)
    {
        return $"{baseOid}.{interfaceIndex}";
    }

    /// <summary>
    /// Get OID for a specific table entry
    /// </summary>
    public static string GetTableOid(string baseOid, params int[] indices)
    {
        return $"{baseOid}.{string.Join(".", indices)}";
    }
}

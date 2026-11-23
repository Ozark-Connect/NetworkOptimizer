namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Standard InfluxDB field names written by the monitoring pipeline. Custom OIDs that
/// match both an OID and its field name here are superseded by built-in polling and
/// removed automatically on first load.
/// </summary>
public static class InfluxFieldNames
{
    public const string FanSpeedRpm = "fan_speed_rpm";

    /// <summary>Negotiated link speed from the UniFi Console port table, on <c>interface_counters</c>.</summary>
    public const string LinkSpeedMbps = "link_speed_mbps";

    /// <summary>Negotiated duplex from the UniFi Console port table, on <c>interface_counters</c>.</summary>
    public const string LinkFullDuplex = "link_full_duplex";

    /// <summary>SNMP ifLastChange in hundredths of a second since boot, on <c>interface_counters</c>.</summary>
    public const string LastChange = "last_change";

    // Console port_table fields, on the port's console series of <c>interface_counters</c>
    // (written by LanPortSnapshotRecorder; kept apart from the SNMP fields of the same names).

    /// <summary>Whether the port was up in the console snapshot.</summary>
    public const string LinkUp = "link_up";

    /// <summary>Whether the port auto-negotiates (false = speed forced in UniFi).</summary>
    public const string LinkAutoneg = "link_autoneg";

    /// <summary>The switch's own count of link-down events since boot.</summary>
    public const string LinkDownCount = "link_down_count";

    /// <summary>STP state changes since boot, summed over MST instances.</summary>
    public const string StpStateChangeCount = "stp_state_change_count";

    /// <summary>Console port_table receive errors.</summary>
    public const string PortRxErrors = "pt_rx_errors";

    /// <summary>Console port_table transmit errors.</summary>
    public const string PortTxErrors = "pt_tx_errors";

    /// <summary>Console port_table receive drops.</summary>
    public const string PortRxDropped = "pt_rx_dropped";

    /// <summary>Console port_table transmit drops.</summary>
    public const string PortTxDropped = "pt_tx_dropped";

    /// <summary>Console port_table received packets.</summary>
    public const string PortRxPackets = "pt_rx_packets";

    /// <summary>Console port_table sent packets.</summary>
    public const string PortTxPackets = "pt_tx_packets";

    /// <summary>Console port_table received broadcast frames.</summary>
    public const string PortRxBroadcast = "pt_rx_broadcast";

    /// <summary>Console port_table received multicast frames.</summary>
    public const string PortRxMulticast = "pt_rx_multicast";

    /// <summary>MAC addresses learned on the port.</summary>
    public const string MacTableCount = "mac_table_count";

    /// <summary>The port's last link-up time to its device, epoch seconds.</summary>
    public const string ConnectedAt = "connected_at";

    /// <summary>PoE draw in watts.</summary>
    public const string PoePower = "poe_power";

    /// <summary>PoE current in milliamps.</summary>
    public const string PoeCurrent = "poe_current";

    /// <summary>PoE voltage in volts.</summary>
    public const string PoeVoltage = "poe_voltage";

    /// <summary>The powered device's PoE class number.</summary>
    public const string PoeClass = "poe_class";

    /// <summary>Whether the PoE power-up succeeded.</summary>
    public const string PoeGood = "poe_good";

    /// <summary>The port's PoE mode ("auto", "off", "pasv24", ...).</summary>
    public const string PoeMode = "poe_mode";

    /// <summary>Gateway switch chip receive CRC errors (On-Site Agent, swconfig).</summary>
    public const string ChipRxCrcErrors = "chip_rx_crc_errors";

    /// <summary>Gateway switch chip transmit CRC errors (On-Site Agent, swconfig).</summary>
    public const string ChipTxCrcErrors = "chip_tx_crc_errors";

    /// <summary>
    /// Gateway switch chip link speed (On-Site Agent, swconfig). Its own field, never <see cref="LinkSpeedMbps"/>:
    /// the chip's port N is ethN only on some gateways, so a chip speed under an ethN name can be another port's.
    /// </summary>
    public const string ChipLinkSpeedMbps = "chip_link_speed_mbps";

    /// <summary>Gateway switch chip link state (On-Site Agent, swconfig); its own field for the same reason.</summary>
    public const string ChipLinkUp = "chip_link_up";

    /// <summary>Gateway switch chip transmit discards on the port itself (On-Site Agent, swconfig).</summary>
    public const string ChipOutDiscards = "chip_out_discards";

    /// <summary>Gateway switch chip pause frames received (On-Site Agent, swconfig).</summary>
    public const string ChipRxPauseFrames = "chip_rx_pause_frames";

    /// <summary>Gateway switch chip late collisions (On-Site Agent, swconfig).</summary>
    public const string ChipLateCollisions = "chip_late_collisions";

    // Device-level, on <c>device_health</c>.

    /// <summary>The device's total PoE draw in watts.</summary>
    public const string PoeTotalUsedPower = "poe_total_used_power";

    /// <summary>The device's PoE budget in watts.</summary>
    public const string PoeTotalMaxPower = "poe_total_max_power";

    /// <summary>The device's effective PoE budget in watts (after the power supply limit).</summary>
    public const string PoeTotalMaxEffectivePower = "poe_total_max_effective_power";

    /// <summary>The console's near-PoE-limit flag.</summary>
    public const string PoeNearLimit = "poe_near_limit";

    /// <summary>Mesh uplink signal in dBm (the child's view).</summary>
    public const string UplinkSignalDbm = "uplink_signal_dbm";

    /// <summary>Mesh uplink noise floor in dBm.</summary>
    public const string UplinkNoiseDbm = "uplink_noise_dbm";

    /// <summary>Mesh uplink transmit PHY rate in Kbps (summed over MLO links).</summary>
    public const string UplinkTxRateKbps = "uplink_tx_rate_kbps";

    /// <summary>Mesh uplink receive PHY rate in Kbps (summed over MLO links).</summary>
    public const string UplinkRxRateKbps = "uplink_rx_rate_kbps";

    /// <summary>The device's uplink parent MAC, so parent changes show in history.</summary>
    public const string UplinkParentMac = "uplink_parent_mac";

    /// <summary>Mesh hop count from the wired network.</summary>
    public const string UplinkDepth = "uplink_depth";

    /// <summary>MLO links the backhaul runs on.</summary>
    public const string UplinkMloLinkCount = "uplink_mlo_links";

    /// <summary>Per-MLO-member field: <c>uplink_mlo{n}_signal_dbm</c> etc. for member n (0-based, by band order).</summary>
    public static string UplinkMloField(int member, string what) => $"uplink_mlo{member}_{what}";

    /// <summary>The AP Agent's carrier_changes for the AP's wired uplink (eth0).</summary>
    public const string UplinkCarrierChanges = "uplink_carrier_changes";

    /// <summary>The AP Agent's receive CRC count for the AP's wired uplink (ethtool -S).</summary>
    public const string UplinkRxCrcErrors = "uplink_rx_crc_errors";
}

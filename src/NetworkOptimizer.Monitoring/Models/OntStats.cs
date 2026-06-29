namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// External ONT (Optical Network Terminal) statistics from device web UI scraping.
/// Covers DDM optics readings, link state, and module identity - the same data
/// as SFP DDM but sourced from the ISP's device rather than the gateway SFP slot.
/// </summary>
public class OntStats
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DeviceHost { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceModel { get; set; } = "";

    // DDM optics readings
    /// <summary>Receive optical power in dBm</summary>
    public double? RxPowerDbm { get; set; }

    /// <summary>Transmit optical power in dBm</summary>
    public double? TxPowerDbm { get; set; }

    /// <summary>Transceiver temperature in degrees Celsius</summary>
    public double? TemperatureC { get; set; }

    /// <summary>Supply voltage in volts</summary>
    public double? VoltageV { get; set; }

    /// <summary>TX laser bias current in milliamps</summary>
    public double? BiasMa { get; set; }

    // Link and module info
    /// <summary>PON type: GPON, XGS-PON, EPON, etc.</summary>
    public string? PonType { get; set; }

    /// <summary>Link state: Up, Down, etc.</summary>
    public string? LinkState { get; set; }

    /// <summary>WAN operational status: Up, Down</summary>
    public string? OperationalStatus { get; set; }

    /// <summary>SFP module vendor name</summary>
    public string? VendorName { get; set; }

    /// <summary>SFP module part number</summary>
    public string? VendorPn { get; set; }

    /// <summary>SFP module serial number</summary>
    public string? VendorSn { get; set; }

    /// <summary>Laser wavelength in nm</summary>
    public string? WaveLength { get; set; }

    /// <summary>PON ONU activation state (ITU-T G.984.3 / G.9807.1)</summary>
    public PonLinkState PonLinkStatus { get; set; }

    /// <summary>Broadband provisioned speed in Mbps (BWP, not SFP link rate)</summary>
    public int? BwpSpeedMbps { get; set; }

    /// <summary>SFP physical link speed in Mbps (only populated when reported by the device)</summary>
    public int? SfpLinkSpeedMbps { get; set; }

    // Error counters
    /// <summary>FEC error count - the data-loss signal: uncorrectable FEC codewords where the device
    /// distinguishes them (corrected codewords are benign and excluded). Cumulative.</summary>
    public long? FecErrors { get; set; }

    /// <summary>BIP (bit-interleaved-parity) error count. Cumulative; reads 0 on a healthy link.</summary>
    public long? BipErrors { get; set; }

    /// <summary>Estimated distance to OLT in meters</summary>
    public double? Distance { get; set; }

    /// <summary>Seconds the PON link has been continuously up</summary>
    public long? LinkUptimeSeconds { get; set; }

    /// <summary>Upstream OLT vendor reported by the ONT (e.g. "CALX" for Calix)</summary>
    public string? OltVendor { get; set; }

    /// <summary>Upstream OLT model reported by the ONT (e.g. "E7")</summary>
    public string? OltModel { get; set; }
}

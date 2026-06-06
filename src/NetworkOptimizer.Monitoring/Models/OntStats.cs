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

    // Error counters
    /// <summary>FEC corrected error count</summary>
    public long? FecErrors { get; set; }

    /// <summary>BIP error count</summary>
    public long? BipErrors { get; set; }

    /// <summary>Estimated distance to OLT in meters</summary>
    public double? Distance { get; set; }
}

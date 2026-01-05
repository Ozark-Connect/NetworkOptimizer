using System.Text.Json.Serialization;

namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Network mode for cellular connection
/// </summary>
public enum CellularNetworkMode
{
    /// <summary>Unknown or no signal</summary>
    Unknown,
    /// <summary>LTE only (4G)</summary>
    Lte,
    /// <summary>5G Non-Standalone - LTE anchor with 5G NR data (EN-DC)</summary>
    Nr5gNsa,
    /// <summary>5G Standalone - Pure 5G NR without LTE anchor</summary>
    Nr5gSa
}

/// <summary>
/// Comprehensive cellular modem statistics from qmicli commands
/// Supports LTE and 5G NR data from UniFi U5G-Max and similar modems
/// </summary>
public class CellularModemStats
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ModemHost { get; set; } = "";
    public string ModemName { get; set; } = "";

    // Connection status
    public string RegistrationState { get; set; } = "";
    public string Carrier { get; set; } = "";
    public string CarrierMcc { get; set; } = "";
    public string CarrierMnc { get; set; } = "";
    public bool IsRoaming { get; set; }

    // Signal info
    public SignalInfo? Lte { get; set; }
    public SignalInfo? Nr5g { get; set; }

    // Cell info
    public CellInfo? ServingCell { get; set; }
    public List<CellInfo> NeighborCells { get; set; } = new();

    // Band info
    public BandInfo? ActiveBand { get; set; }

    /// <summary>
    /// Detected network mode (LTE, 5G NSA, 5G SA)
    /// </summary>
    public CellularNetworkMode NetworkMode => DetermineNetworkMode();

    /// <summary>
    /// Human-readable network mode string
    /// </summary>
    public string NetworkModeDisplay => NetworkMode switch
    {
        CellularNetworkMode.Lte => "LTE (4G)",
        CellularNetworkMode.Nr5gNsa => "5G NSA (EN-DC)",
        CellularNetworkMode.Nr5gSa => "5G SA",
        _ => "Unknown"
    };

    /// <summary>
    /// Short network mode label for UI badges
    /// </summary>
    public string NetworkModeLabel => NetworkMode switch
    {
        CellularNetworkMode.Lte => "LTE",
        CellularNetworkMode.Nr5gNsa => "5G NSA",
        CellularNetworkMode.Nr5gSa => "5G SA",
        _ => "?"
    };

    /// <summary>
    /// Description of the current network mode
    /// </summary>
    public string NetworkModeDescription => NetworkMode switch
    {
        CellularNetworkMode.Lte => "Connected to 4G LTE network",
        CellularNetworkMode.Nr5gNsa => "5G Non-Standalone: LTE anchor with 5G NR for data (EN-DC mode)",
        CellularNetworkMode.Nr5gSa => "5G Standalone: Pure 5G NR connection without LTE anchor",
        _ => "No cellular connection detected"
    };

    // Computed signal quality (0-100)
    public int SignalQuality => CalculateSignalQuality();

    /// <summary>
    /// Get the primary signal source (5G if it has data, otherwise LTE)
    /// </summary>
    public SignalInfo? PrimarySignal => Nr5g?.Rsrp.HasValue == true ? Nr5g : Lte;

    private CellularNetworkMode DetermineNetworkMode()
    {
        bool hasLte = Lte?.Rsrp.HasValue == true;
        bool hasNr5g = Nr5g?.Rsrp.HasValue == true;

        if (hasLte && hasNr5g)
        {
            // Both LTE and NR5G active = NSA (EN-DC)
            // LTE is the anchor, NR5G provides additional capacity
            return CellularNetworkMode.Nr5gNsa;
        }
        else if (hasNr5g && !hasLte)
        {
            // Only NR5G active = SA (Standalone)
            return CellularNetworkMode.Nr5gSa;
        }
        else if (hasLte)
        {
            // Only LTE active
            return CellularNetworkMode.Lte;
        }

        return CellularNetworkMode.Unknown;
    }

    private int CalculateSignalQuality()
    {
        // Use 5G if it has actual data, otherwise LTE
        var signal = PrimarySignal;
        if (signal == null) return 0;

        // Composite quality using RSRP, RSRQ, and SNR with weighted scoring
        // RSRP: 50% weight (primary strength indicator)
        // SNR:  30% weight (signal-to-noise, critical for throughput)
        // RSRQ: 20% weight (reference signal quality)

        double totalWeight = 0;
        double weightedScore = 0;

        // RSRP: -80 dBm (excellent) to -120 dBm (poor)
        if (signal.Rsrp.HasValue)
        {
            var rsrp = signal.Rsrp.Value;
            var rsrpScore = Math.Clamp((rsrp + 120) * 2.5, 0, 100);
            weightedScore += rsrpScore * 0.5;
            totalWeight += 0.5;
        }

        // SNR: 30 dB (excellent) to 0 dB (poor)
        if (signal.Snr.HasValue)
        {
            var snr = signal.Snr.Value;
            var snrScore = Math.Clamp(snr * (100.0 / 30.0), 0, 100);
            weightedScore += snrScore * 0.3;
            totalWeight += 0.3;
        }

        // RSRQ: -3 dB (excellent) to -20 dB (poor)
        if (signal.Rsrq.HasValue)
        {
            var rsrq = signal.Rsrq.Value;
            var rsrqScore = Math.Clamp((rsrq + 20) * (100.0 / 17.0), 0, 100);
            weightedScore += rsrqScore * 0.2;
            totalWeight += 0.2;
        }

        if (totalWeight == 0) return 0;

        return (int)(weightedScore / totalWeight);
    }
}

/// <summary>
/// Signal strength and quality metrics
/// </summary>
public class SignalInfo
{
    /// <summary>Reference Signal Received Power (dBm) - primary signal strength indicator</summary>
    public double? Rsrp { get; set; }

    /// <summary>Reference Signal Received Quality (dB) - signal quality</summary>
    public double? Rsrq { get; set; }

    /// <summary>Received Signal Strength Indicator (dBm)</summary>
    public double? Rssi { get; set; }

    /// <summary>Signal-to-Noise Ratio (dB)</summary>
    public double? Snr { get; set; }

    /// <summary>Signal bars (1-5) based on RSRP</summary>
    public int Bars => CalculateBars();

    private int CalculateBars()
    {
        if (!Rsrp.HasValue) return 0;
        var rsrp = Rsrp.Value;

        if (rsrp >= -80) return 5;
        if (rsrp >= -90) return 4;
        if (rsrp >= -100) return 3;
        if (rsrp >= -110) return 2;
        if (rsrp >= -120) return 1;
        return 0;
    }

    /// <summary>Human-readable signal quality</summary>
    public string Quality => Bars switch
    {
        5 => "Excellent",
        4 => "Good",
        3 => "Fair",
        2 => "Poor",
        1 => "Very Poor",
        _ => "No Signal"
    };
}

/// <summary>
/// Cell tower information
/// </summary>
public class CellInfo
{
    /// <summary>Physical Cell ID</summary>
    public int PhysicalCellId { get; set; }

    /// <summary>Global Cell ID</summary>
    public string? GlobalCellId { get; set; }

    /// <summary>Tracking Area Code</summary>
    public string? Tac { get; set; }

    /// <summary>EARFCN (LTE) or ARFCN (5G NR) - frequency channel number</summary>
    public int? Earfcn { get; set; }

    /// <summary>Band description (e.g., "E-UTRA band 2: 1900 PCS")</summary>
    public string? BandDescription { get; set; }

    /// <summary>PLMN (MCC + MNC)</summary>
    public string? Plmn { get; set; }

    /// <summary>Signal metrics for this cell</summary>
    public SignalInfo? Signal { get; set; }

    /// <summary>Timing advance in microseconds</summary>
    public int? TimingAdvance { get; set; }

    /// <summary>Is this the serving cell?</summary>
    public bool IsServing { get; set; }
}

/// <summary>
/// Active RF band information
/// </summary>
public class BandInfo
{
    /// <summary>Radio interface type (lte, nr5g, etc.)</summary>
    public string RadioInterface { get; set; } = "";

    /// <summary>Active band class (e.g., "eutran-2", "n77")</summary>
    public string BandClass { get; set; } = "";

    /// <summary>Active channel number</summary>
    public int Channel { get; set; }

    /// <summary>Bandwidth in MHz</summary>
    public int? BandwidthMhz { get; set; }

    /// <summary>Human-readable band name</summary>
    public string BandName => GetBandName();

    private string GetBandName()
    {
        // Common LTE/5G band mappings
        return BandClass.ToLowerInvariant() switch
        {
            "eutran-2" => "Band 2 (1900 MHz PCS)",
            "eutran-3" => "Band 3 (1800 MHz)",
            "eutran-4" => "Band 4 (AWS-1)",
            "eutran-5" => "Band 5 (850 MHz)",
            "eutran-7" => "Band 7 (2600 MHz)",
            "eutran-12" => "Band 12 (700 MHz)",
            "eutran-13" => "Band 13 (700 MHz)",
            "eutran-14" => "Band 14 (700 MHz FirstNet)",
            "eutran-17" => "Band 17 (700 MHz)",
            "eutran-25" => "Band 25 (1900 MHz)",
            "eutran-26" => "Band 26 (850 MHz)",
            "eutran-30" => "Band 30 (2300 MHz)",
            "eutran-41" => "Band 41 (2500 MHz TDD)",
            "eutran-66" => "Band 66 (AWS-3)",
            "eutran-71" => "Band 71 (600 MHz)",
            "n2" => "n2 (1900 MHz)",
            "n5" => "n5 (850 MHz)",
            "n41" => "n41 (2500 MHz)",
            "n71" => "n71 (600 MHz)",
            "n77" => "n77 (3700 MHz C-Band)",
            "n78" => "n78 (3500 MHz)",
            "n260" => "n260 (39 GHz mmWave)",
            "n261" => "n261 (28 GHz mmWave)",
            _ => BandClass
        };
    }
}

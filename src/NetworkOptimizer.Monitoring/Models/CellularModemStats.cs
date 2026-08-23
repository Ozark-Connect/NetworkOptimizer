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
/// EN-DC (5G NSA) state, derived from the modem's 5G NSA availability and DCNR
/// restriction flags. Distinguishes the one case a radio reset can fix - the network
/// permits EN-DC but the serving cell does not offer it - from cases where it cannot.
/// </summary>
public enum EnDcState
{
    /// <summary>System info was unavailable, so nothing can be concluded.</summary>
    Unknown,
    /// <summary>An NR secondary cell is attached - 5G is up.</summary>
    Attached,
    /// <summary>The network is withholding EN-DC from this UE. No anchor change can help.</summary>
    NetworkRestricted,
    /// <summary>The serving cell offers EN-DC and the NR leg has not attached yet.</summary>
    AnchorCapable,
    /// <summary>EN-DC is permitted but the serving cell does not offer it.</summary>
    AnchorMissing
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
    public string ModemModel { get; set; } = "";

    /// <summary>
    /// The modem's own build, whatever its vendor calls it - "software version" and
    /// "firmware version" are the same thing on this class of device, so they share one
    /// field and the display picks the vendor's word. Null when the provider cannot read it.
    ///
    /// On a host-plus-module device (a GL.iNet router carrying a Quectel module) this is the
    /// MODULE's firmware, not the router's: the modem is what is being monitored. Matches
    /// StarlinkStats.SoftwareVersion, which is where this pair started; a HardwareVersion
    /// belongs beside it if a provider ever reports one.
    /// </summary>
    public string? SoftwareVersion { get; set; }

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

    // EN-DC (5G NSA) anchor state, from the modem's system info
    /// <summary>Serving cell advertises EN-DC support. Null when system info was unavailable.</summary>
    public bool? Is5gNsaAvailable { get; set; }

    /// <summary>Network is withholding EN-DC from this UE. Null when system info was unavailable.</summary>
    public bool? IsDcnrRestricted { get; set; }

    /// <summary>
    /// Why 5G is or is not up, from the pair of EN-DC flags. The pair matters: a missing
    /// NR leg is only worth acting on when the network permits EN-DC and the serving cell
    /// is the one withholding it.
    /// </summary>
    public EnDcState EnDc
    {
        get
        {
            if (!Is5gNsaAvailable.HasValue || !IsDcnrRestricted.HasValue)
                return EnDcState.Unknown;
            if (Nr5g?.Rsrp.HasValue == true)
                return EnDcState.Attached;
            if (IsDcnrRestricted.Value)
                return EnDcState.NetworkRestricted;
            return Is5gNsaAvailable.Value ? EnDcState.AnchorCapable : EnDcState.AnchorMissing;
        }
    }

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

        bool is5g = Nr5g?.Rsrp.HasValue == true;

        // Composite quality using RSRP, RSRQ, and SNR with weighted scoring
        // RSRP: 50% weight (primary strength indicator)
        // SNR:  30% weight (signal-to-noise, critical for throughput)
        // RSRQ: 20% weight (reference signal quality)

        double totalWeight = 0;
        double weightedScore = 0;

        // RSRP: Use different ranges for 5G vs LTE (industry standards)
        // 5G NR: -80 dBm (excellent) to -110 dBm (poor) - tighter thresholds
        // LTE:   -90 dBm (excellent) to -120 dBm (poor) - more relaxed
        if (signal.Rsrp.HasValue)
        {
            var rsrp = signal.Rsrp.Value;
            double rsrpScore;
            if (is5g)
            {
                // 5G: -80 = 100%, -110 = 0% (30 dBm range)
                rsrpScore = Math.Clamp((rsrp + 110) * (100.0 / 30.0), 0, 100);
            }
            else
            {
                // LTE: -90 = 100%, -120 = 0% (30 dBm range)
                rsrpScore = Math.Clamp((rsrp + 120) * (100.0 / 30.0), 0, 100);
            }
            weightedScore += rsrpScore * 0.5;
            totalWeight += 0.5;
        }

        // SNR: 30 dB (excellent) to 0 dB (poor) - same for both technologies
        if (signal.Snr.HasValue)
        {
            var snr = signal.Snr.Value;
            var snrScore = Math.Clamp(snr * (100.0 / 30.0), 0, 100);
            weightedScore += snrScore * 0.3;
            totalWeight += 0.3;
        }

        // RSRQ: -3 dB (excellent) to -20 dB (poor) - same for both technologies
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

    /// <summary>
    /// eNodeB (site) identifier: the upper 20 bits of the 28-bit LTE cell id.
    /// Null when <see cref="GlobalCellId"/> is absent or non-numeric.
    /// </summary>
    public int? EnbId => ParsedCellId >> 8;

    /// <summary>
    /// Sector within the site: the low 8 bits of the LTE cell id. Neighbor cells
    /// sharing an <see cref="EnbId"/> are other sectors of the same tower.
    /// </summary>
    public int? SectorId => ParsedCellId & 0xFF;

    private int? ParsedCellId =>
        int.TryParse(GlobalCellId, out var eci) && eci > 0 ? eci : null;
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

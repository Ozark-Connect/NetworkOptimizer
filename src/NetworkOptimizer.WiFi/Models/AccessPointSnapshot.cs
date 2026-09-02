using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Point-in-time snapshot of an access point's Wi-Fi state
/// </summary>
public class AccessPointSnapshot
{
    /// <summary>AP MAC address (unique identifier)</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>User-assigned name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Model name (e.g., "U7 Pro")</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Firmware version</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>IP address</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Overall device satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Total connected clients across all radios</summary>
    public int TotalClients { get; set; }

    /// <summary>
    /// Clients the AP Agent holds right now: exact and seconds old, where the console's count lags
    /// its report interval and keeps a client the agent saw leave. Null on an AP no agent covers.
    /// </summary>
    public int? MeasuredClientCount { get; set; }

    /// <summary>The measured count where there is one, the console's otherwise.</summary>
    public int EffectiveClientCount => MeasuredClientCount ?? TotalClients;

    /// <summary>Per-radio details</summary>
    public List<RadioSnapshot> Radios { get; set; } = new();

    /// <summary>Per-SSID/radio details (VAP table)</summary>
    public List<VapSnapshot> Vaps { get; set; } = new();

    /// <summary>When this snapshot was taken</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Connection status derived from the UniFi device <c>state</c> (see UniFiDeviceStateMap).
    /// Drives the status indicator; distinguishes provisioning/updating (yellow) from offline (grey).
    /// </summary>
    public DeviceStatus Status { get; set; } = new(DeviceStatusKind.Online, "Online");

    /// <summary>
    /// Whether this AP is fully online and actionable (the Online bucket: connected or
    /// update-available). A provisioning/updating AP is not offline, but also not yet actionable -
    /// use <see cref="Status"/> for display, this for gating actions like the backhaul re-scan.
    /// </summary>
    public bool IsOnline => Status.IsOnline;

    /// <summary>Whether this AP is a mesh child (has wireless uplink to another AP)</summary>
    public bool IsMeshChild { get; set; }

    /// <summary>
    /// Whether this AP has a dedicated scan radio (an all-band radio that measures the RF
    /// environment without taking a serving radio off-channel). When true, a quick scan is
    /// non-disruptive to clients and mesh uplinks; when false, scanning borrows the serving radio
    /// and briefly interrupts that band. Sourced from the device's scan_radio_table.
    /// </summary>
    public bool HasDedicatedScanRadio { get; set; }

    /// <summary>MAC address of the mesh parent AP (if this is a mesh child)</summary>
    public string? MeshParentMac { get; set; }

    /// <summary>Radio band used for mesh uplink (if mesh child)</summary>
    public RadioBand? MeshUplinkBand { get; set; }

    /// <summary>Channel used for mesh uplink (if mesh child)</summary>
    public int? MeshUplinkChannel { get; set; }

    /// <summary>
    /// wpa_supplicant STA backhaul interface for the mesh uplink (e.g. "vwiresta7"), if this is
    /// a mesh child. Null for wired APs. Used to target the backhaul re-scan/roam over SSH.
    /// </summary>
    public string? MeshUplinkInterface { get; set; }

    /// <summary>Signal strength of mesh uplink in dBm (if mesh child)</summary>
    public int? MeshUplinkSignalDbm { get; set; }

    /// <summary>TX rate of mesh uplink in Mbps (if mesh child)</summary>
    public int? MeshUplinkTxRateMbps { get; set; }

    /// <summary>RX rate of mesh uplink in Mbps (if mesh child)</summary>
    public int? MeshUplinkRxRateMbps { get; set; }

    /// <summary>Name of the mesh parent AP (resolved from MAC, if mesh child)</summary>
    public string? MeshParentName { get; set; }

    /// <summary>Whether the mesh uplink is an MLO (Wi-Fi 7 multi-link) backhaul</summary>
    public bool MeshUplinkIsMlo { get; set; }

    /// <summary>
    /// Every band/channel the mesh uplink occupies: the STA link plus any other MLO links.
    /// A classic backhaul yields one entry; non-mesh APs none.
    /// </summary>
    public IEnumerable<(RadioBand Band, int? Channel)> MeshUplinkBandChannels
    {
        get
        {
            if (MeshUplinkBand.HasValue)
                yield return (MeshUplinkBand.Value, MeshUplinkChannel);
            foreach (var link in MeshUplinkLinks)
            {
                if (link.Band.HasValue && link.Band != MeshUplinkBand)
                    yield return (link.Band.Value, link.Channel);
            }
        }
    }

    /// <summary>Whether the mesh uplink occupies the given band (any link, on MLO).</summary>
    public bool MeshUplinkUsesBand(RadioBand band) =>
        MeshUplinkBandChannels.Any(x => x.Band == band);

    /// <summary>Whether the mesh uplink occupies the given band and channel (any link, on MLO).</summary>
    public bool MeshUplinkUsesChannel(RadioBand band, int channel) =>
        MeshUplinkBandChannels.Any(x => x.Band == band && x.Channel == channel);

    /// <summary>
    /// Whether a mesh backhaul this AP participates in - as child or as parent - occupies the
    /// given band. The parent side reads its children's links; the child side its own uplink.
    /// </summary>
    public bool MeshBackhaulUsesBand(RadioBand band) =>
        MeshUplinkUsesBand(band) ||
        MeshChildren.Any(c => c.UplinkBand == band || c.Links.Any(l => l.Band == band));

    /// <summary>
    /// Per-link detail of the mesh uplink, child's perspective (TX toward the parent). From the
    /// child's own uplink.mlo_links when it reports them (signal as the child measures it), else
    /// the parent's downlink_table flipped; empty when neither end reports links.
    /// </summary>
    public List<MeshLinkInfo> MeshUplinkLinks { get; set; } = new();

    /// <summary>Mesh children connected to this AP (if mesh parent)</summary>
    public List<MeshChildInfo> MeshChildren { get; set; } = new();

    /// <summary>Whether AFC (Automated Frequency Coordination) is enabled on this AP</summary>
    public bool IsAfcEnabled { get; set; }

    /// <summary>AFC state: "disabled", "location_acquired", etc.</summary>
    public string? AfcState { get; set; }
}

/// <summary>
/// Summary info about a mesh child AP connected to a parent
/// </summary>
public class MeshChildInfo
{
    public string Mac { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? SignalDbm { get; set; }
    public int? TxRateMbps { get; set; }
    public int? RxRateMbps { get; set; }
    public RadioBand? UplinkBand { get; set; }

    /// <summary>Whether the backhaul to this child is an MLO (Wi-Fi 7 multi-link) pairing</summary>
    public bool IsMlo { get; set; }

    /// <summary>Per-link detail, parent's perspective (TX toward the child). Empty when the
    /// parent's downlink_table reported nothing and the values above came from the child.</summary>
    public List<MeshLinkInfo> Links { get; set; } = new();
}

/// <summary>
/// One radio link of a mesh backhaul. TX/RX direction and the signal reading follow the owning
/// collection's end: the child's own on <see cref="AccessPointSnapshot.MeshUplinkLinks"/> (when
/// it reports its links), the parent's on <see cref="MeshChildInfo.Links"/>.
/// </summary>
public class MeshLinkInfo
{
    public RadioBand? Band { get; set; }
    public int? Channel { get; set; }
    public int? SignalDbm { get; set; }
    public int? TxRateMbps { get; set; }
    public int? RxRateMbps { get; set; }
}

/// <summary>
/// Point-in-time snapshot of a single radio on an AP
/// </summary>
public class RadioSnapshot
{
    /// <summary>Radio identifier (wifi0, wifi1, wifi2)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Band: 2.4GHz, 5GHz, or 6GHz</summary>
    public RadioBand Band { get; set; }

    /// <summary>Current channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width in MHz (20, 40, 80, 160)</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>Extension channel number for 40 MHz+ bonding (from radio_table_stats)</summary>
    public int? ExtChannel { get; set; }

    /// <summary>
    /// Center of the operating block as a channel number, reported by the AP Agent. At 320 MHz
    /// the primary sits in one block of each of two overlapping channelizations and the console
    /// never says which; this does. Null without an agent, and the span falls back to a guess.
    /// </summary>
    public int? CenterChannel { get; set; }

    /// <summary>The channel is set by hand in UniFi Network (radio_table channel is a number, not "auto").</summary>
    public bool ChannelIsFixed { get; set; }

    /// <summary>The TX power mode is set by hand in UniFi Network (anything but "auto").</summary>
    public bool TxPowerIsFixed { get; set; }

    /// <summary>
    /// This radio's width differs from the most common width on its band across the site, which
    /// is the best available reading of a deliberate per-AP width; UniFi carries no override flag.
    /// </summary>
    public bool WidthIsOverride { get; set; }

    /// <summary>
    /// Airtime busy percent measured by the AP Agent in the last two minutes (the radio's own
    /// <c>cu_total</c>). Null without an agent, or when its reading is stale. Named apart from
    /// <see cref="ChannelUtilization"/>, the console's figure, which keeps feeding every rule.
    /// </summary>
    public int? MeasuredUtilization { get; set; }

    /// <summary>Of <see cref="MeasuredUtilization"/>, the percent that is this radio's own traffic (<c>cu_self_tx</c> plus <c>cu_self_rx</c>).</summary>
    public int? MeasuredSelfAirtime { get; set; }

    /// <summary>Of <see cref="MeasuredUtilization"/>, the percent that is other transmitters (<c>cu_interf</c>).</summary>
    public int? MeasuredInterference { get; set; }

    /// <summary>The radio's latest measured noise floor in dBm, from the AP Agent.</summary>
    public int? MeasuredNoiseFloor { get; set; }

    /// <summary>The median of the last hour's measured noise floors, once an hour's worth exists.</summary>
    public int? MeasuredNoiseFloorHour { get; set; }

    /// <summary>When the measured fields were read (UTC).</summary>
    public DateTime? MeasuredAt { get; set; }

    /// <summary>Current TX power in dBm</summary>
    public int? TxPower { get; set; }

    /// <summary>TX power mode (auto, high, medium, low, custom)</summary>
    public string? TxPowerMode { get; set; }

    /// <summary>Minimum TX power in dBm (from device capability)</summary>
    public int? MinTxPower { get; set; }

    /// <summary>Maximum TX power in dBm (from device capability)</summary>
    public int? MaxTxPower { get; set; }

    /// <summary>Antenna gain in dBi</summary>
    public int? AntennaGain { get; set; }

    /// <summary>EIRP (Effective Isotropic Radiated Power) = TxPower + AntennaGain</summary>
    public int? Eirp => TxPower.HasValue ? TxPower.Value + (AntennaGain ?? 0) : null;

    /// <summary>Radio satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Number of connected clients</summary>
    public int? ClientCount { get; set; }

    /// <summary>Channel utilization percentage (0-100)</summary>
    public int? ChannelUtilization { get; set; }

    /// <summary>Interference level (0-100)</summary>
    public int? Interference { get; set; }

    /// <summary>TX retries as percentage</summary>
    public double? TxRetriesPct { get; set; }

    /// <summary>Whether min RSSI steering is enabled (hard disconnect)</summary>
    public bool MinRssiEnabled { get; set; }

    /// <summary>Min RSSI threshold if enabled (dBm)</summary>
    public int? MinRssi { get; set; }

    /// <summary>Whether Roaming Assistant is enabled (soft BSS transition, 5 GHz only)</summary>
    public bool RoamingAssistantEnabled { get; set; }

    /// <summary>Roaming Assistant RSSI threshold (dBm)</summary>
    public int? RoamingAssistantRssi { get; set; }

    /// <summary>Whether DFS channels are available</summary>
    public bool HasDfs { get; set; }

    /// <summary>Whether this radio supports 802.11be (Wi-Fi 7). Required for MLO.</summary>
    public bool Is11Be { get; set; }

    /// <summary>
    /// Active antenna mode name (e.g., "Internal", "OMNI", "Combined").
    /// Resolved from radio_table.antenna_id → antenna_table.name.
    /// Null for indoor APs with no switchable modes (antenna_id = -1).
    /// </summary>
    public string? AntennaMode { get; set; }
}

/// <summary>
/// Radio frequency band
/// </summary>
public enum RadioBand
{
    Unknown,
    Band2_4GHz,
    Band5GHz,
    Band6GHz
}

/// <summary>
/// Point-in-time snapshot of a Virtual AP (SSID on a radio)
/// </summary>
public class VapSnapshot
{
    /// <summary>SSID name</summary>
    public string Essid { get; set; } = string.Empty;

    /// <summary>BSSID (MAC of this VAP)</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>Radio band</summary>
    public RadioBand Band { get; set; }

    /// <summary>Channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Number of connected clients</summary>
    public int? ClientCount { get; set; }

    /// <summary>Satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Average client signal strength (dBm)</summary>
    public int? AvgClientSignal { get; set; }

    /// <summary>Whether this is a guest network</summary>
    public bool IsGuest { get; set; }

    /// <summary>TX bytes since last reset</summary>
    public long? TxBytes { get; set; }

    /// <summary>RX bytes since last reset</summary>
    public long? RxBytes { get; set; }

    /// <summary>TX retries count</summary>
    public long? TxRetries { get; set; }

    /// <summary>WiFi TX attempts</summary>
    public long? WifiTxAttempts { get; set; }

    /// <summary>WiFi TX dropped</summary>
    public long? WifiTxDropped { get; set; }
}

public static class RadioBandExtensions
{
    /// <summary>
    /// Convert UniFi radio code to RadioBand enum
    /// </summary>
    public static RadioBand FromUniFiCode(string? code)
    {
        return code?.ToLowerInvariant() switch
        {
            "ng" => RadioBand.Band2_4GHz,
            "na" => RadioBand.Band5GHz,
            "6e" => RadioBand.Band6GHz,
            _ => RadioBand.Unknown
        };
    }

    /// <summary>
    /// Get display string for band
    /// </summary>
    public static string ToDisplayString(this RadioBand band)
    {
        return band switch
        {
            RadioBand.Band2_4GHz => "2.4 GHz",
            RadioBand.Band5GHz => "5 GHz",
            RadioBand.Band6GHz => "6 GHz",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Get UniFi code for band
    /// </summary>
    public static string ToUniFiCode(this RadioBand band)
    {
        return band switch
        {
            RadioBand.Band2_4GHz => "ng",
            RadioBand.Band5GHz => "na",
            RadioBand.Band6GHz => "6e",
            _ => ""
        };
    }

    /// <summary>
    /// Get propagation band string for use with PropagationService and MaterialAttenuation.
    /// </summary>
    public static string ToPropagationBand(this RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => "2.4",
        RadioBand.Band5GHz => "5",
        RadioBand.Band6GHz => "6",
        _ => "5"
    };

    /// <summary>
    /// Check if a data band string (e.g. "ng", "na", "6e", "2.4", "5", "6") matches
    /// a propagation band string ("2.4", "5", "6"). Null data band is treated as a match.
    /// </summary>
    public static bool MatchesPropagationBand(string? dataBand, string propagationBand)
    {
        if (dataBand == null) return true;
        if (string.Equals(dataBand, propagationBand, StringComparison.OrdinalIgnoreCase)) return true;
        var resolved = FromUniFiCode(dataBand);
        return resolved != RadioBand.Unknown && string.Equals(resolved.ToPropagationBand(), propagationBand, StringComparison.OrdinalIgnoreCase);
    }
}

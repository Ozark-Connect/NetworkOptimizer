namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Soak-period state for one AP radio: the channels it recently moved OFF of, which the
/// optimizer must not recommend hopping back to until the new channel has had time to prove
/// itself (and the outcome memory has accumulated measured data for it). Applies to all bands.
/// </summary>
public class ChannelSoakInfo
{
    /// <summary>Channels this radio occupied within the soak window and then left.
    /// Never contains the radio's current channel.</summary>
    public HashSet<int> SoakedChannels { get; init; } = new();

    /// <summary>When the most recent channel change happened (UTC)</summary>
    public DateTimeOffset LastChangeAt { get; init; }

    /// <summary>When the soak period ends (UTC): last change + soak window</summary>
    public DateTimeOffset SoakEndsAt { get; init; }

    /// <summary>
    /// The AP Agent's one-hour verdict on the move that started this soak, when the AP is covered
    /// and the hour has elapsed. Read by exactly one thing: the soak escape, which lets a radio
    /// leave a channel measured Worse at one hour instead of waiting on the console's report.
    /// </summary>
    public MoveOutcome? MeasuredOutcome { get; init; }

    /// <summary>When the verdict was reached (UTC).</summary>
    public DateTimeOffset? MeasuredAt { get; init; }
}

/// <summary>How a channel move measured after an hour, from the agent's own airtime readings.</summary>
public enum MoveOutcome
{
    /// <summary>Interference fell by more than the dead band.</summary>
    Improved,

    /// <summary>Within the dead band either way.</summary>
    Same,

    /// <summary>Interference rose by more than the dead band.</summary>
    Worse
}

/// <summary>
/// One persisted daily outcome bucket for an AP radio config, storage-neutral so the engine
/// project stays decoupled from the database layer. Sums divide by <see cref="SampleCount"/>
/// to recover averages.
/// </summary>
/// <param name="Channel">Control channel the samples were attributed to</param>
/// <param name="WidthMhz">Channel width in MHz; 0 when unknown</param>
/// <param name="UtilizationSum">Sum of channel utilization percentages</param>
/// <param name="InterferenceSum">Sum of interference percentages</param>
/// <param name="TxRetrySum">Sum of TX retry percentages</param>
/// <param name="SampleCount">Number of samples in the bucket</param>
/// <param name="LastSampleAt">Most recent sample in the bucket (UTC)</param>
/// <param name="CenterChannel">Measured block center as a channel number, or null (console-sourced, or before the agent reported it)</param>
/// <param name="NoiseFloorSum">Sum of measured noise floors (dBm) over the samples that carried one; null when none did</param>
/// <param name="NoiseFloorSamples">How many samples carried a noise floor</param>
public record ChannelOutcomeBucket(
    int Channel,
    int WidthMhz,
    double UtilizationSum,
    double InterferenceSum,
    double TxRetrySum,
    int SampleCount,
    DateTimeOffset LastSampleAt,
    int? CenterChannel = null,
    double? NoiseFloorSum = null,
    int NoiseFloorSamples = 0);

/// <summary>
/// One persisted neighbor sighting for an AP radio, storage-neutral so the engine project
/// stays decoupled from the database layer. Lets a serving radio keep (age-decayed) neighbor
/// evidence for channels it isn't currently on.
/// </summary>
/// <param name="ApMac">Observing AP MAC</param>
/// <param name="Band">Radio band the neighbor was seen on</param>
/// <param name="Bssid">Neighbor BSSID</param>
/// <param name="Channel">Control channel the neighbor was seen on</param>
/// <param name="WidthMhz">Neighbor channel width in MHz; 0 when unknown</param>
/// <param name="SignalDbm">Strongest observed signal in dBm</param>
/// <param name="SightingCount">Collection cycles this neighbor has been seen - the persistence signal</param>
/// <param name="LastSeenAt">Most recent sighting (UTC) - drives age decay</param>
/// <param name="Ssid">Neighbor SSID, if any</param>
public record RememberedNeighborSighting(
    string ApMac,
    RadioBand Band,
    string Bssid,
    int Channel,
    int WidthMhz,
    int SignalDbm,
    int SightingCount,
    DateTimeOffset LastSeenAt,
    string? Ssid);

/// <summary>
/// One aggregate of client PHY-rate telemetry for an AP radio, bucketed by channel, signal band
/// and day. Storage-neutral so the engine project stays decoupled from the telemetry store.
///
/// A "window" is a fixed slice of time (15 minutes) in which the radio carried real traffic; idle
/// windows are excluded upstream, because an idle client's rate decays and describes nothing about
/// the channel. Windows rather than raw samples because per-client aggregation is not affordable:
/// client_mac is a field rather than a tag, so isolating clients requires a pivot over every raw
/// point, which measured 33s against 1s for the windowed form.
/// </summary>
/// <param name="Channel">Control channel the radio was on for these windows</param>
/// <param name="WidthMhz">
/// The client's own channel width, which is commonly narrower than the AP's configuration - so
/// this buckets clients by capability as well as catching a width change on the radio itself.
/// </param>
/// <param name="SignalBandDbm">Signal bucket (dBm, rounded down to a fixed step)</param>
/// <param name="Day">UTC day, used for the distinct-day evidence floor</param>
/// <param name="WindowCount">Active windows in this bucket</param>
/// <param name="MeanTxRateMbps">Mean AP-to-client PHY rate across those windows</param>
public record ClientRateSample(
    int Channel,
    int WidthMhz,
    int SignalBandDbm,
    DateTime Day,
    int WindowCount,
    double MeanTxRateMbps);

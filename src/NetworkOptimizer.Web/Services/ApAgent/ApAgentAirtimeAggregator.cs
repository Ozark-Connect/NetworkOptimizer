namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// One radio-hour of agent-measured airtime, finalized once the hour has elapsed. Weight-wise it
/// is the agent-side equivalent of one UniFi hourly report row, so agent-covered and
/// console-covered radios feed the channel outcome memory at the same one-sample-per-hour rate.
/// </summary>
/// <param name="ApMac">AP MAC (lowercase, colon-separated).</param>
/// <param name="Band">Radio band code the outcome table uses - "ng", "na", or "6e".</param>
/// <param name="HourUtc">Start of the UTC hour the readings fall into.</param>
/// <param name="Channel">Control channel that held the majority of the hour's readings.</param>
/// <param name="WidthMhz">Channel width in MHz at those readings; 0 when the agent did not report one.</param>
/// <param name="AvgUtilization">Mean channel utilization percent over the winning config's readings.</param>
/// <param name="AvgInterference">Mean interference percent over the winning config's readings.</param>
/// <param name="ReadingCount">How many readings the winning config contributed.</param>
/// <param name="LastSampleUtc">Timestamp of the winning config's newest reading.</param>
/// <param name="CenterChannel">The block center the winning config's readings carried most often, as a channel number; null when none carried one.</param>
/// <param name="AvgNoiseFloor">Mean noise floor (dBm) over the winning config's readings that carried one; null when none did.</param>
public sealed record ApAgentAirtimeHour(
    string ApMac,
    string Band,
    DateTime HourUtc,
    int Channel,
    int WidthMhz,
    double AvgUtilization,
    double AvgInterference,
    int ReadingCount,
    DateTime LastSampleUtc,
    int? CenterChannel = null,
    double? AvgNoiseFloor = null);

/// <summary>
/// Folds the AP Agent's continuous airtime readings into per-radio hourly aggregates for the
/// channel outcome memory. In-memory only, and deliberately NOT a writer: the channel memory
/// sweep consumes finalized hours inside its own atomic commit, so an agent-covered hour
/// replaces the console's sample for that hour instead of joining it, and a restart merely
/// hands pending hours back to the console path.
///
/// An hour is attributed to the (channel, width) that held the majority of its readings, and
/// its averages are computed over that config's readings ONLY - blending a mid-hour channel
/// change's two configs would charge each channel with the other's airtime.
/// </summary>
public sealed class ApAgentAirtimeAggregator
{
    /// <summary>
    /// Readings the winning config needs before its hour can stand in for the console's. Five
    /// minutes at the 30 s radio cadence: enough averaging to beat the console's single hourly
    /// value, while a coverage blip of a few readings leaves the hour to the console path.
    /// </summary>
    public const int MinReadingsPerHour = 10;

    /// <summary>Matches the channel memory sweep's console lookback; older hours are unreachable either way.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Hard cap on retained hours (oldest evicted first), bounding a site that never sweeps.</summary>
    public const int MaxFinalizedHours = 4096;

    private readonly object _lock = new();
    private readonly Dictionary<(string Mac, string Band), OpenHour> _open = new();
    private readonly SortedDictionary<(DateTime Hour, string Mac, string Band), ApAgentAirtimeHour> _finalized = new();

    /// <summary>
    /// Records one airtime reading. Unknown bands, absent channels, and out-of-range utilization
    /// values (counter sentinels) are dropped rather than clamped into a plausible number.
    /// </summary>
    public void Record(string apMac, string? bandToken, int channel, int widthMhz,
        double cuTotal, double cuInterference, DateTime atUtc,
        int? centerChannel = null, int? noiseFloorDbm = null)
    {
        var band = MapBandCode(bandToken);
        if (band.Length == 0 || channel <= 0) return;
        if (cuTotal < 0 || cuTotal > 100) return;
        var interference = Math.Clamp(cuInterference, 0, 100);

        var mac = (apMac ?? "").Trim().ToLowerInvariant();
        if (mac.Length == 0) return;
        var hour = FloorHour(atUtc);

        lock (_lock)
        {
            var key = (mac, band);
            if (_open.TryGetValue(key, out var open) && open.HourUtc != hour)
            {
                FinalizeLocked(mac, band, open);
                open = null;
            }
            if (open == null)
            {
                open = new OpenHour(hour);
                _open[key] = open;
            }

            var segKey = (channel, widthMhz > 0 ? widthMhz : 0);
            if (!open.Segments.TryGetValue(segKey, out var seg))
            {
                seg = new Segment();
                open.Segments[segKey] = seg;
            }
            seg.Count++;
            seg.UtilizationSum += cuTotal;
            seg.InterferenceSum += interference;
            if (atUtc > seg.LastAt) seg.LastAt = atUtc;
            if (centerChannel is > 0)
                seg.CenterCounts[centerChannel.Value] = seg.CenterCounts.GetValueOrDefault(centerChannel.Value) + 1;
            // A floor above 0 dBm or below -120 is a counter sentinel, not a reading.
            if (noiseFloorDbm is < 0 and > -120)
            {
                seg.NoiseCount++;
                seg.NoiseSum += noiseFloorDbm.Value;
            }
        }
    }

    /// <summary>
    /// Finalized hours in [startUtc, endExclusiveUtc). Open hours that ended before
    /// endExclusiveUtc are finalized first, so a radio whose agent went quiet mid-hour still
    /// surrenders its partial hour to the sweep instead of holding it open forever.
    /// </summary>
    public IReadOnlyList<ApAgentAirtimeHour> GetFinalizedHours(DateTime startUtc, DateTime endExclusiveUtc)
    {
        lock (_lock)
        {
            foreach (var (key, open) in _open.Where(kv => kv.Value.HourUtc < FloorHour(endExclusiveUtc)).ToList())
            {
                FinalizeLocked(key.Mac, key.Band, open);
                _open.Remove(key);
            }

            return _finalized.Values
                .Where(h => h.HourUtc >= startUtc && h.HourUtc < endExclusiveUtc)
                .ToList();
        }
    }

    /// <summary>Drops finalized hours before the cutoff - called after the sweep commits them.</summary>
    public void PruneBefore(DateTime cutoffUtc)
    {
        lock (_lock)
        {
            foreach (var key in _finalized.Keys.Where(k => k.Hour < cutoffUtc).ToList())
                _finalized.Remove(key);
        }
    }

    /// <summary>
    /// Maps the agent's band tokens ("2.4" / "5" / "6", or mca's "ng" / "na" / "6e") onto the
    /// outcome table's band codes. Unknown tokens yield "" rather than a guess.
    /// </summary>
    public static string MapBandCode(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        "2.4" or "ng" => "ng",
        "5" or "na" => "na",
        "6" or "6e" or "ax6e" or "6g" => "6e",
        _ => "",
    };

    private void FinalizeLocked(string mac, string band, OpenHour open)
    {
        // Majority residency decides the hour's config; ties go to the more recent segment.
        var winner = open.Segments
            .OrderByDescending(kv => kv.Value.Count)
            .ThenByDescending(kv => kv.Value.LastAt)
            .FirstOrDefault();
        if (winner.Value == null || winner.Value.Count < MinReadingsPerHour) return;

        var seg = winner.Value;
        // Majority center, like majority config: the block behind a primary changes only with a
        // channel change, so within one winning segment the readings agree except across one.
        int? center = seg.CenterCounts.Count == 0
            ? null
            : seg.CenterCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
        double? noise = seg.NoiseCount > 0 ? seg.NoiseSum / seg.NoiseCount : null;
        _finalized[(open.HourUtc, mac, band)] = new ApAgentAirtimeHour(
            mac, band, open.HourUtc,
            winner.Key.Channel, winner.Key.Width,
            seg.UtilizationSum / seg.Count,
            seg.InterferenceSum / seg.Count,
            seg.Count,
            seg.LastAt,
            center,
            noise);

        var retentionCutoff = open.HourUtc - Retention;
        while (_finalized.Count > 0)
        {
            var oldest = _finalized.Keys.First();
            if (oldest.Hour >= retentionCutoff && _finalized.Count <= MaxFinalizedHours) break;
            _finalized.Remove(oldest);
        }
    }

    private static DateTime FloorHour(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    private sealed class OpenHour
    {
        public OpenHour(DateTime hourUtc) => HourUtc = hourUtc;
        public DateTime HourUtc { get; }
        public Dictionary<(int Channel, int Width), Segment> Segments { get; } = new();
    }

    private sealed class Segment
    {
        public int Count;
        public double UtilizationSum;
        public double InterferenceSum;
        public DateTime LastAt;
        public readonly Dictionary<int, int> CenterCounts = new();
        public int NoiseCount;
        public double NoiseSum;
    }
}

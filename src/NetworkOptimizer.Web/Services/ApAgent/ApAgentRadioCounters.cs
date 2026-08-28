namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One radio's counter movement over the window between two readings.</summary>
/// <param name="Radio">Interface name.</param>
/// <param name="Band">Band token as the agent reported it.</param>
/// <param name="Channel">Operating channel at the end of the window.</param>
/// <param name="At">When the window closed.</param>
/// <param name="WindowSeconds">Seconds the window spans.</param>
/// <param name="CycleDelta">Movement in cycle_cnt, the radio's free-running clock.</param>
/// <param name="RxClearDelta">Movement in rx_clear_cnt, the cycles the channel was seen busy.</param>
/// <param name="TxFrameDelta">Movement in tx_frame_cnt, the cycles this radio spent transmitting.</param>
/// <param name="PhyErrDelta">Movement in phy_err_cnt.</param>
/// <param name="PdevResets">Cumulative pdev_resets.</param>
/// <param name="PdevResetDelta">Movement in pdev_resets.</param>
/// <param name="BusyRatio">RxClear over Cycle, or null when either is unusable.</param>
/// <param name="Wedged">Whether the window matched the CCA wedge signature.</param>
public sealed record ApRadioWindow(
    string Radio,
    string? Band,
    int Channel,
    DateTime At,
    double WindowSeconds,
    long? CycleDelta,
    long? RxClearDelta,
    long? TxFrameDelta,
    long? PhyErrDelta,
    long? PdevResets,
    long? PdevResetDelta,
    double? BusyRatio,
    bool Wedged);

/// <summary>
/// Reads the radio counters and differences them.
///
/// The counters are unsigned 32-bit and wrap, and 0xFFFFFFFF is the value a tool reports when it
/// has no reading rather than a real count. Both have to be handled here: a wrap read as a plain
/// subtraction is a delta of minus four billion, and the sentinel read as a count is a wedge
/// detector that fires on a radio nobody measured.
/// </summary>
public static class ApAgentRadioCounters
{
    /// <summary>The value the radio-stats tools report in place of a reading they do not have.</summary>
    public const long Sentinel = 4294967295;

    /// <summary>One past the top of the unsigned 32-bit range.</summary>
    public const long Modulus = 4294967296;

    /// <summary>A counter this high is close enough to the top of the range for a wrap to be plausible.</summary>
    private const long WrapHighWater = Modulus / 4 * 3;

    /// <summary>Counter names this server keeps out of the roughly 80 KB the agent serves.</summary>
    public const string Cycle = "cycle_cnt";

    /// <summary>Cycles the clear-channel assessment saw the medium busy.</summary>
    public const string RxClear = "rx_clear_cnt";

    /// <summary>Cycles this radio spent transmitting.</summary>
    public const string TxFrame = "tx_frame_cnt";

    /// <summary>PHY errors.</summary>
    public const string PhyErr = "phy_err_cnt";

    /// <summary>Radio resets the driver performed without saying so anywhere else.</summary>
    public const string PdevResets = "pdev_resets";

    /// <summary>Reads one counter, treating absent, negative, and sentinel values alike as no reading.</summary>
    public static long? Read(IReadOnlyDictionary<string, long>? counters, string name)
    {
        if (counters == null || !counters.TryGetValue(name, out var value)) return null;
        if (value < 0 || value >= Sentinel) return null;
        return value;
    }

    /// <summary>
    /// Movement between two readings. A counter that went backwards is only a wrap when it was near
    /// the top of the range and came back near the bottom; anything else is a reset, which has no
    /// meaningful delta and must not be guessed at.
    /// </summary>
    public static long? Delta(long? previous, long? current)
    {
        if (previous is not { } prev || current is not { } cur) return null;
        if (cur >= prev) return cur - prev;
        if (prev >= WrapHighWater && cur < Modulus / 4) return cur + Modulus - prev;
        return null;
    }
}

/// <summary>
/// Holds the previous counter reading per radio for one access point and turns each new reading
/// into a window. State is in memory only: a restart costs one window, not a false delta.
/// </summary>
public sealed class ApAgentRadioHealthTracker
{
    private readonly Dictionary<string, Reading> _previous = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Differences a fresh /radios reading against the one before it.</summary>
    public IReadOnlyList<ApRadioWindow> Observe(IReadOnlyList<ApAgentRadioAirtime> radios)
    {
        var windows = new List<ApRadioWindow>(radios.Count);

        foreach (var radio in radios)
        {
            var reading = new Reading(
                ApAgentRadioCounters.Read(radio.Counters, ApAgentRadioCounters.Cycle),
                ApAgentRadioCounters.Read(radio.Counters, ApAgentRadioCounters.RxClear),
                ApAgentRadioCounters.Read(radio.Counters, ApAgentRadioCounters.TxFrame),
                ApAgentRadioCounters.Read(radio.Counters, ApAgentRadioCounters.PhyErr),
                ApAgentRadioCounters.Read(radio.Counters, ApAgentRadioCounters.PdevResets),
                radio.At);

            if (!_previous.TryGetValue(radio.Radio, out var prior))
            {
                _previous[radio.Radio] = reading;
                continue;
            }
            _previous[radio.Radio] = reading;

            var cycle = ApAgentRadioCounters.Delta(prior.Cycle, reading.Cycle);
            var rxClear = ApAgentRadioCounters.Delta(prior.RxClear, reading.RxClear);
            var txFrame = ApAgentRadioCounters.Delta(prior.TxFrame, reading.TxFrame);
            var phyErr = ApAgentRadioCounters.Delta(prior.PhyErr, reading.PhyErr);
            var resets = ApAgentRadioCounters.Delta(prior.PdevResets, reading.PdevResets);

            var busy = cycle is > 0 && rxClear.HasValue ? rxClear.Value / (double)cycle.Value : (double?)null;

            windows.Add(new ApRadioWindow(
                radio.Radio,
                radio.Band,
                radio.Channel,
                reading.At,
                (reading.At - prior.At).TotalSeconds,
                cycle,
                rxClear,
                txFrame,
                phyErr,
                reading.PdevResets,
                resets,
                busy,
                ApAgentRadioWedgeDetector.MatchesSignature(cycle, rxClear, txFrame)));
        }

        return windows;
    }

    private sealed record Reading(long? Cycle, long? RxClear, long? TxFrame, long? PhyErr, long? PdevResets, DateTime At);
}

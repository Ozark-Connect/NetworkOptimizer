namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Detects the 6 GHz CCA wedge from the radio counters.
///
/// The measured signature is that clear-channel assessment reports the medium busy for very nearly
/// every cycle while the radio transmits nothing at all: Rx Clear approaches Cycle with a Tx Frame
/// delta of zero. Healthy idle looks nothing like it, because the only thing making the channel
/// busy is our own beacons, so Rx Clear moves with Tx Frame and both stay far below Cycle.
///
/// pdev_resets is the early warning rather than the fault: it climbed on one radio for about ten
/// hours before clients abandoned the band, and nothing appears in dmesg or syslog while it does.
/// The signal is the RATE against that radio's own baseline, never the presence of resets: on U7
/// hardware 6 GHz resets continuously while 2.4 and 5 GHz sit at zero for their whole uptime, so
/// comparing a radio to its siblings marks every healthy U7 access point as failing forever.
/// </summary>
public sealed class ApAgentRadioWedgeDetector
{
    /// <summary>How close Rx Clear must sit to Cycle to count as "the medium is never clear".</summary>
    public const double BusyRatioThreshold = 0.98;

    /// <summary>
    /// Consecutive windows the signature must hold before an alert. One window is a reading, two
    /// are a condition, and at a 30 s window that is a minute of a radio that has stopped talking.
    /// </summary>
    public const int ConfirmWindows = 2;

    private readonly Dictionary<string, int> _consecutive = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _alerted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether one window matches the wedge. A zero Tx Frame delta on its own is an idle radio; it
    /// is only the wedge when the radio also believes the medium is permanently busy.
    /// </summary>
    public static bool MatchesSignature(long? cycleDelta, long? rxClearDelta, long? txFrameDelta)
    {
        if (cycleDelta is not { } cycle || cycle <= 0) return false;
        if (rxClearDelta is not { } rxClear || rxClear < 0) return false;
        if (txFrameDelta is not 0) return false;
        return rxClear >= cycle * BusyRatioThreshold;
    }

    /// <summary>
    /// Feeds one window in and reports whether this is the moment to alert. It returns true once
    /// per episode: a wedge that persists must not re-raise on every pass, and a radio that
    /// recovers re-arms.
    /// </summary>
    public bool Observe(string radio, bool wedged)
    {
        if (!wedged)
        {
            _consecutive.Remove(radio);
            _alerted.Remove(radio);
            return false;
        }

        var count = _consecutive.GetValueOrDefault(radio) + 1;
        _consecutive[radio] = count;

        if (count < ConfirmWindows || _alerted.Contains(radio)) return false;
        _alerted.Add(radio);
        return true;
    }

    /// <summary>
    /// Radios that reset while every sibling on the same access point stayed still. A reset on all
    /// of them at once is a firmware event rather than one radio going wrong, so it is not reported.
    /// </summary>
    /// <summary>
    /// Resets per minute a radio must exceed before its rate is worth reporting. The measured wedge
    /// ran at about 1.6/s (~96/min); the documented idle residual on a healthy 6 GHz radio is 4-9
    /// per 60-90s. This sits well above the residual and far below the fault.
    /// </summary>
    public const double ResetRateFloorPerMinute = 30.0;

    /// <summary>How far above its own baseline a radio's reset rate must climb to count.</summary>
    public const double ResetRateMultiple = 5.0;

    /// <summary>
    /// Radios whose reset rate has climbed well above their own recent baseline. Both conditions
    /// must hold: a radio with a naturally busy baseline should not alert until it gets materially
    /// worse, and a quiet radio should not alert on a handful of resets.
    /// </summary>
    /// <param name="windows">This pass's windows for one access point.</param>
    /// <param name="baselineRatePerMinute">
    /// That radio's established resets-per-minute, keyed by radio name. A radio with no baseline yet
    /// is skipped rather than assumed quiet, so a first observation cannot alert on its own history.
    /// </param>
    public static IReadOnlyList<ApRadioWindow> ElevatedResets(
        IReadOnlyList<ApRadioWindow> windows,
        IReadOnlyDictionary<string, double> baselineRatePerMinute)
    {
        var elevated = new List<ApRadioWindow>();

        foreach (var w in windows)
        {
            if (w.PdevResetDelta is not > 0 || w.WindowSeconds <= 0) continue;
            if (!baselineRatePerMinute.TryGetValue(w.Radio, out var baseline)) continue;

            var rate = w.PdevResetDelta.Value / (w.WindowSeconds / 60.0);
            if (rate < ResetRateFloorPerMinute) continue;
            if (rate < baseline * ResetRateMultiple) continue;

            elevated.Add(w);
        }

        return elevated;
    }

}

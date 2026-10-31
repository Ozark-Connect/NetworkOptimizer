using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>Which signals put a window over its target's baseline.</summary>
[Flags]
public enum DegradationSignal
{
    None = 0,
    Loss = 1,
    Rtt = 2
}

/// <summary>
/// One closed evaluation window of a target's latency samples.
/// </summary>
/// <param name="Start">Window start (aligned to the window length).</param>
/// <param name="MedianRttMs">Median of the window's per-burst average RTTs; null when nothing answered.</param>
/// <param name="LossPct">Pooled loss across the window's bursts.</param>
/// <param name="Probes">Pings sent in the window.</param>
/// <param name="Signal">What was over baseline. Always None while the target has no baseline yet.</param>
/// <param name="Excluded">Kept out of the baseline: an outage, or an episode the target was degraded in.</param>
/// <param name="Judged">Whether a baseline existed to judge it against.</param>
public sealed record DegradationWindow(DateTime Start, double? MedianRttMs, double LossPct, int Probes,
    DegradationSignal Signal, bool Excluded, bool Judged);

/// <summary>
/// A monitored target's degradation state against its OWN baseline, fed one ping burst at a time.
///
/// Samples fold into fixed evaluation windows (5 min by default). Each closed window is judged against
/// the trailing 24 h of the target's windows: median RTT and mean loss, leaving out windows that were
/// part of an outage or of an episode the target was degraded in, so an evening of degradation never
/// becomes the yardstick it is measured by. A target is degraded after
/// <see cref="IspHealthOptions.PathDegradationConsecutiveWindows"/> degraded windows in a row and clears
/// after as many clean ones.
///
/// Not thread-safe; the owner serializes access.
/// </summary>
public sealed class TargetDegradation
{
    /// <summary>A window this lossy is an outage, never a baseline, whatever the caller says.</summary>
    internal const double OutageLossPct = 50.0;

    /// <summary>Bursts a window needs before its median RTT is read.</summary>
    internal const int MinRttSamples = 3;

    private readonly IspHealthOptions _options;
    private readonly TimeSpan _windowLength;
    private readonly LinkedList<DegradationWindow> _windows = new();

    private DateTime? _openStart;
    private readonly List<double> _openRtts = new();
    private int _openSent;
    private int _openReceived;

    private int _degradedRun;
    private int _cleanRun;
    private DateTime? _runStart;

    public TargetDegradation(string targetId, IspHealthOptions options)
    {
        TargetId = targetId;
        _options = options;
        _windowLength = TimeSpan.FromMinutes(Math.Max(1, options.PathDegradationWindowMinutes));
    }

    public string TargetId { get; }

    /// <summary>Sustained degradation: the consecutive-window test passed and has not cleared.</summary>
    public bool Degraded { get; private set; }

    /// <summary>Start of the first window of the run that made the target degraded.</summary>
    public DateTime? DegradedSince { get; private set; }

    /// <summary>End of the window that cleared the last degradation.</summary>
    public DateTime? ClearedAt { get; private set; }

    /// <summary>Baseline median RTT as of the last closed window.</summary>
    public double? BaselineRttMs { get; private set; }

    /// <summary>Baseline mean loss as of the last closed window.</summary>
    public double? BaselineLossPct { get; private set; }

    /// <summary>Worst window loss since <see cref="DegradedSince"/>.</summary>
    public double PeakLossPct { get; private set; }

    /// <summary>Worst window median RTT since <see cref="DegradedSince"/>.</summary>
    public double? PeakRttMs { get; private set; }

    public DegradationWindow? LastWindow => _windows.Last?.Value;

    public DateTime? LastSampleAt { get; private set; }

    /// <summary>Whether enough history exists to judge a window at all.</summary>
    public bool HasBaseline => BaselineRttMs.HasValue || BaselineLossPct.HasValue;

    /// <summary>How far the last window's RTT rose over baseline, for ranking degraded targets.</summary>
    public double RttRiseMs => LastWindow?.MedianRttMs is double m && BaselineRttMs is double b ? Math.Max(0, m - b) : 0;

    /// <summary>Whether the target was heard from recently enough to say anything about it.</summary>
    public bool IsFresh(DateTime now) =>
        LastSampleAt is DateTime t && now - t <= TimeSpan.FromMinutes(_options.PathTargetStaleMinutes);

    /// <summary>
    /// A clean control: fresh, judged against a baseline, not degraded, and its latest window clean.
    /// A target with no baseline yet is unknown, never clean.
    /// </summary>
    public bool IsClean(DateTime now) =>
        IsFresh(now) && !Degraded && LastWindow is { Judged: true, Signal: DegradationSignal.None };

    /// <summary>Whether any window starting at or after <paramref name="from"/> was over baseline. The dense
    /// evidence a monitored hop's series gives a trace (spec 5.3), at window granularity.</summary>
    public bool? ElevatedSince(DateTime from)
    {
        var judged = _windows.Where(w => w.Start + _windowLength > from && w.Judged).ToList();
        if (judged.Count == 0) return null;
        return judged.Any(w => w.Signal != DegradationSignal.None);
    }

    /// <summary>
    /// Pre-loads history (5-min means from InfluxDB) so a restart does not cost a day of baseline.
    /// Seeded windows only feed the baseline; they never move the degraded state. Samples recorded
    /// before the seed arrived win: history is only laid in ahead of the first window already held.
    /// </summary>
    /// <param name="windows">Window start, mean RTT, mean loss.</param>
    /// <param name="excluded">Spans to leave out of the baseline (past episodes).</param>
    public void Seed(IEnumerable<(DateTime Start, double? RttMs, double? LossPct)> windows,
        IReadOnlyList<(DateTime Start, DateTime End)> excluded)
    {
        var before = _windows.First?.Value.Start ?? _openStart ?? DateTime.MaxValue;
        foreach (var (start, rtt, loss) in windows.Where(w => w.Start < before).OrderByDescending(w => w.Start))
        {
            if (rtt is null && loss is null) continue;
            if (_windows.First is { } first && start >= first.Value.Start) continue;
            var lossPct = loss ?? 0;
            var inEpisode = excluded.Any(e => start < e.End && start + _windowLength > e.Start);
            // A seeded window stands for a full window of bursts, so its loss reads as if it had them.
            _windows.AddFirst(new DegradationWindow(start, rtt, lossPct, _options.PathLossMinProbes,
                DegradationSignal.None, inEpisode || lossPct >= OutageLossPct, Judged: false));
        }
        Trim();
        RefreshBaseline(DateTime.MaxValue);
    }

    /// <summary>
    /// Folds one ping burst in. Returns the window this sample closed, if it opened a new one.
    /// A sample older than the open window is dropped: replayed backlog says nothing about now.
    /// </summary>
    /// <param name="excludeClosing">Asked once when a window closes: keep it out of the baseline?</param>
    public DegradationWindow? Record(DateTime time, double? rttAvgMs, int sent, int received, Func<bool> excludeClosing)
    {
        var start = CongestionDetector.FloorTime(time, _windowLength);
        if (_openStart is DateTime open && start < open) return null;

        DegradationWindow? closed = null;
        if (_openStart is DateTime current && start > current)
            closed = Close(current, excludeClosing());

        if (_openStart != start)
        {
            _openStart = start;
            _openRtts.Clear();
            _openSent = 0;
            _openReceived = 0;
        }
        if (rttAvgMs is double r) _openRtts.Add(r);
        _openSent += Math.Max(0, sent);
        _openReceived += Math.Clamp(received, 0, Math.Max(0, sent));
        LastSampleAt = time;
        return closed;
    }

    private DegradationWindow Close(DateTime start, bool exclude)
    {
        // Judged against the baseline as it stood BEFORE this window, so it never grades itself.
        RefreshBaseline(start);
        var median = _openRtts.Count >= MinRttSamples ? SeriesStats.Median(_openRtts) : null;
        var loss = _openSent == 0 ? 0 : 100.0 * (_openSent - _openReceived) / _openSent;
        var judged = HasBaseline;

        var signal = DegradationSignal.None;
        if (judged)
        {
            var baseLoss = BaselineLossPct ?? 0;
            if (_openSent >= _options.PathLossMinProbes
                && loss >= Math.Max(_options.PathLossMinPct, baseLoss + _options.PathLossDeltaPct))
                signal |= DegradationSignal.Loss;
            if (median is double m && BaselineRttMs is double b
                && m >= b + Math.Max(_options.PathRttMinDeltaMs, b * _options.PathRttDeltaFraction))
                signal |= DegradationSignal.Rtt;
        }

        var window = new DegradationWindow(start, median, loss, _openSent, signal,
            exclude || loss >= OutageLossPct, judged);
        _windows.AddLast(window);
        Trim();
        Advance(window);
        return window;
    }

    private void Advance(DegradationWindow w)
    {
        if (!w.Judged)
        {
            _degradedRun = _cleanRun = 0;
            _runStart = null;
            return;
        }

        var need = Math.Max(1, _options.PathDegradationConsecutiveWindows);
        if (w.Signal != DegradationSignal.None)
        {
            if (_degradedRun == 0) _runStart = w.Start;
            _degradedRun++;
            _cleanRun = 0;
            if (!Degraded && _degradedRun >= need)
            {
                Degraded = true;
                DegradedSince = _runStart;
                PeakLossPct = 0;
                PeakRttMs = null;
                foreach (var run in _windows.Where(x => x.Start >= _runStart))
                    TrackPeak(run);
            }
            else if (Degraded)
            {
                TrackPeak(w);
            }
        }
        else
        {
            _cleanRun++;
            _degradedRun = 0;
            if (Degraded && _cleanRun >= need)
            {
                Degraded = false;
                ClearedAt = w.Start + _windowLength;
            }
        }
    }

    private void TrackPeak(DegradationWindow w)
    {
        if (w.LossPct > PeakLossPct) PeakLossPct = w.LossPct;
        if (w.MedianRttMs is double m && (PeakRttMs is null || m > PeakRttMs)) PeakRttMs = m;
    }

    /// <param name="before">Windows starting before this count; <see cref="DateTime.MaxValue"/> for all of them.</param>
    private void RefreshBaseline(DateTime before)
    {
        var anchor = before == DateTime.MaxValue ? _windows.Last?.Value.Start.AddTicks(1) : before;
        var usable = anchor is DateTime a
            ? _windows.Where(w => !w.Excluded && w.Start < a && w.Start >= a - TimeSpan.FromHours(_options.PathBaselineHours)).ToList()
            : new List<DegradationWindow>();
        if (usable.Count < _options.PathBaselineMinWindows)
        {
            BaselineRttMs = null;
            BaselineLossPct = null;
            return;
        }
        BaselineRttMs = SeriesStats.Median(usable.Where(w => w.MedianRttMs.HasValue).Select(w => w.MedianRttMs!.Value).ToList());
        BaselineLossPct = usable.Average(w => w.LossPct);
    }

    private void Trim()
    {
        if (_windows.Last is not { } last) return;
        var keepFrom = last.Value.Start - TimeSpan.FromHours(_options.PathBaselineHours) - _windowLength;
        while (_windows.First is { } first && first.Value.Start < keepFrom)
            _windows.RemoveFirst();
    }
}

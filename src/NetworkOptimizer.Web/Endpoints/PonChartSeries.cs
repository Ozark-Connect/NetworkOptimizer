using System.Globalization;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// The PON series both chart tabs draw. An ONT attached to a monitored SFP module and a standalone
/// one report the same counters, so they get one builder rather than two lists kept in step by hand.
/// </summary>
public static class PonChartSeries
{
    /// <summary>
    /// Project supplemental PON points into the chart payload, converting cumulative
    /// counters to per-interval deltas (CM Stats style - cumulative lines are unreadable).
    /// A negative step means the ONT rebooted and reset its counters; that interval's
    /// delta is null (a gap) rather than a bogus spike. Null when the module has no
    /// supplemental data, so the UI can hide the PON section entirely.
    /// </summary>
    public static List<object>? Build(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 0 }) return null;

        static long? Delta(long? cur, long? prev) =>
            cur is long c && prev is long p && c >= p ? c - p : null;

        var ordered = points.OrderBy(p => p.Time).ToList();
        var items = new List<object>(ordered.Count);
        MonitoringInfluxClient.PonSeriesPoint? prev = null;
        foreach (var p in ordered)
        {
            items.Add(new
            {
                time = p.Time.ToString("o"),
                state = p.PonLinkStatus,
                statePrev = p.PonLinkStatusPrev,
                onuId = p.OnuId,
                dsFec = p.DsFecEnabled,
                usFec = p.UsFecEnabled,
                respTime = p.OnuResponseTime,
                uptime = p.SfpUptimeS,
                ploamMs = p.PloamElapsedMs,
                lanLink = p.LanLinkStatus,
                lanMode = p.LanMode,
                olt = DisplayFormatters.FormatOltVendor(p.OltVendorId, p.OltModel),
                oltVersion = p.OltVersion,
                registered = DisplayFormatters.FormatPonRegistration(p.RegistrationStatus, p.KeyExchangeStatus),
                firmware = p.FirmwareVersion,
                bip = Delta(p.BipErrors, prev?.BipErrors),
                fec = Delta(p.FecErrors, prev?.FecErrors),
                fecCorr = Delta(p.FecCorrectedWords, prev?.FecCorrectedWords),
                hec = Delta(p.HecUncorrected, prev?.HecUncorrected),
                hecCorr = Delta(p.HecCorrected, prev?.HecCorrected),
                bwmapCorr = Delta(p.BwmapCorrected, prev?.BwmapCorrected),
                bwmapUncorr = Delta(p.BwmapUncorrected, prev?.BwmapUncorrected),
                gemTx = Delta(p.GemTxFrames, prev?.GemTxFrames),
                gemTxIdle = Delta(p.GemTxIdleFrames, prev?.GemTxIdleFrames),
                gemRx = Delta(p.GemRxFrames, prev?.GemRxFrames),
                gemDrop = Delta(p.GemRxDropped, prev?.GemRxDropped),
                dsUtil = DownstreamUtilization(p, prev)?.Percent,
                dsUsedMbps = DownstreamUtilization(p, prev)?.Mbps,
                dsOwnMbps = OwnDownstreamMbps(p, prev),
                pathLoss = p.PathLossDb,
                oltLaunch = p.OltLaunchPowerDbm,
                odnClass = p.OdnClass,
                reachExt = p.ReachExtender,
                allocLost = Delta(p.AllocLost, prev?.AllocLost),
                lanFcs = Delta(p.LanRxFcsErrors, prev?.LanRxFcsErrors),
                lanDrop = Delta(p.LanTxDropEvents, prev?.LanTxDropEvents),
                lanOvfl = Delta(p.LanBufferOverflow, prev?.LanBufferOverflow),
                // Raw cumulative values for the error totals table
                bipTotal = p.BipErrors,
                fecTotal = p.FecErrors,
                fecCorrTotal = p.FecCorrectedWords,
                hecTotal = p.HecUncorrected,
                hecCorrTotal = p.HecCorrected,
                bwmapCorrTotal = p.BwmapCorrected,
                bwmapUncorrTotal = p.BwmapUncorrected,
                allocLostTotal = p.AllocLost,
                gemDropTotal = p.GemRxDropped,
                lanFcsTotal = p.LanRxFcsErrors,
                lanDropTotal = p.LanTxDropEvents,
                lanOvflTotal = p.LanBufferOverflow,
            });
            prev = p;
        }
        return items;
    }

    /// <summary>A GPON downstream frame: 125 us, 38880 bytes at 2.488 Gbit/s.</summary>
    private const double GponFrameBytes = 38880;
    /// <summary>With FEC on, RS(255,239) takes 16 parity bytes from each of the frame's 153 codewords.</summary>
    private const double GponFecParityBytes = 153 * 16;
    private const double GemIdleFrameBytes = 5;
    private const double GponFramesPerSecond = 8000;
    private const long SuperframeModulus = 1L << 30;

    /// <summary>
    /// How much of the PON's downstream was in use over one interval, all ONTs together: the OLT
    /// fills what nobody needs with idle GEM frames, and every ONT counts the same ones. Read
    /// against the superframe counter rather than the clock, so the interval length and the
    /// sampling jitter drop out. The frame header (PCBd, with the bandwidth map) is counted as
    /// in use, so a PON with no traffic at all still reads a little above zero. Null when either
    /// counter is missing, went backwards (a reboot) or the result is outside 0 to 100 percent,
    /// which is what a superframe counter that wrapped more than once produces.
    /// </summary>
    public static (double Percent, double Mbps)? DownstreamUtilization(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint? prev)
    {
        if (prev is null
            || cur.GemRxIdleFrames is not long idle || prev.GemRxIdleFrames is not long idlePrev
            || idle < idlePrev
            || FramesBetween(cur, prev) is not long frames)
            return null;

        var payload = GponFrameBytes - (cur.DsFecEnabled == 1 ? GponFecParityBytes : 0);
        var usedPerFrame = payload - (idle - idlePrev) * GemIdleFrameBytes / frames;
        var percent = usedPerFrame / payload * 100;
        if (percent < 0 || percent > 100) return null;

        return (Math.Round(percent, 3), Math.Round(usedPerFrame * 8 * GponFramesPerSecond / 1e6, 2));
    }

    /// <summary>
    /// Downstream frames between two points, off the superframe counter. The counter sampled
    /// together with the totals is used when both points carry it, so the idle count and its time
    /// base come from one sample; a source that does not serve it (older firmware) falls back to
    /// the live counter, which can be a sample ahead. Never one of each: the two differ by up to a
    /// second's worth of frames. The counter is 30 bits wide, so a smaller value is one wrap.
    /// Null without a pair, or when the counter stood still.
    /// </summary>
    internal static long? FramesBetween(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint prev)
    {
        long sf, sfPrev;
        if (cur.DsSuperframeSampled is long s && prev.DsSuperframeSampled is long sp) { sf = s; sfPrev = sp; }
        else if (cur.DsSuperframe is long l && prev.DsSuperframe is long lp) { sf = l; sfPrev = lp; }
        else return null;

        var frames = sf >= sfPrev ? sf - sfPrev : sf + SuperframeModulus - sfPrev;
        return frames > 0 ? frames : null;
    }

    /// <summary>
    /// This ONT's own downstream over one interval in Mbit/s, from the bytes on its GEM ports, to
    /// set beside the load of the whole PON. Timed by the superframe counter like the PON load
    /// where there is one, by the points' timestamps otherwise. Null when the byte counter is
    /// missing or went backwards: it counts since the GEM flow was created, so a reboot or a
    /// re-provisioning restarts it.
    /// </summary>
    public static double? OwnDownstreamMbps(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint? prev)
    {
        if (prev is null
            || cur.GemRxBytes is not long bytes || prev.GemRxBytes is not long bytesPrev
            || bytes < bytesPrev)
            return null;

        var seconds = FramesBetween(cur, prev) is long frames
            ? frames / GponFramesPerSecond
            : (cur.Time - prev.Time).TotalSeconds;
        if (seconds <= 0) return null;

        return Math.Round((bytes - bytesPrev) * 8 / seconds / 1e6, 3);
    }

    /// <summary>The loss budget of each ODN class the PON-ID message can name (G.984.2), in dB.</summary>
    private static readonly (string Name, double MinDb, double MaxDb)[] OdnClasses =
    {
        ("A", 5, 20), ("B", 10, 25), ("B+", 13, 28), ("C", 15, 30), ("C+", 17, 32),
    };

    /// <summary>A path loss this far above the window's usual level is worth a line.</summary>
    internal const double PathLossRiseHintDb = 2.0;
    /// <summary>Less than this much budget left to the class limit is worth a line.</summary>
    internal const double PathLossNearLimitDb = 3.0;
    /// <summary>The OLT announces its power in 0.1 dB steps; a full step down is a change of level.</summary>
    internal const double LaunchDropHintDb = 1.0;
    /// <summary>The newest points that stand for "now", and the fewest older ones that make a usual level.</summary>
    private const int RecentPoints = 3;
    private const int MinBaselinePoints = 6;
    private const int MaxChanges = 10;

    /// <summary>One change in what the OLT announces about itself, which points at work on the provider's side.</summary>
    public sealed record PonIdChange(string Time, string What, string From, string To);

    /// <summary>
    /// Where the optical path stands, from the window's points: the latest loss against the budget
    /// of the class the OLT names, how far it has moved from the window's usual level and on which
    /// side of the fibre the move is, and every change in the OLT's own announcement.
    /// </summary>
    public sealed record PathLossSummary
    {
        public double? LossDb { get; init; }
        public double? LaunchDbm { get; init; }
        /// <summary>Launch power less loss: the ONT's RX reading the loss was taken from.</summary>
        public double? RxDbm { get; init; }
        public bool ReachExtender { get; init; }
        public string? OdnClass { get; init; }
        public double? ClassMinDb { get; init; }
        public double? ClassMaxDb { get; init; }
        /// <summary>Budget left to the class limit; negative when the loss is over it.</summary>
        public double? MarginDb { get; init; }
        /// <summary>How far the loss is under the class minimum, when it is.</summary>
        public double? BelowMinDb { get; init; }
        /// <summary>The window's usual loss: the median of everything before the newest points.</summary>
        public double? BaselineLossDb { get; init; }
        /// <summary>Recent loss less the usual one. Positive is a path that got worse.</summary>
        public double? LossChangeDb { get; init; }
        /// <summary>Recent announced launch power less the usual one. Negative is the OLT side sending less.</summary>
        public double? LaunchChangeDb { get; init; }
        /// <summary>Recent RX less the usual one.</summary>
        public double? RxChangeDb { get; init; }
        /// <summary>"ok", "notice" or "warning": the worst of the hints below.</summary>
        public string Level { get; init; } = "ok";
        /// <summary>Stable keys of what applies: over_limit, near_limit, below_min, loss_rise, launch_drop.</summary>
        public IReadOnlyList<string> Hints { get; init; } = [];
        public IReadOnlyList<PonIdChange> Changes { get; init; } = [];
    }

    /// <summary>
    /// Null when no point carries anything from a PON-ID message: an OLT that sends none, or a
    /// source that does not relay it. The section then stays off the page. An OLT that names its
    /// class but not its power gets the class and no loss.
    /// </summary>
    public static PathLossSummary? SummarizePathLoss(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 0 }) return null;
        var ordered = points.OrderBy(p => p.Time).ToList();
        if (!ordered.Any(p => p.PathLossDb.HasValue || p.OltLaunchPowerDbm.HasValue || p.OdnClass.HasValue || p.ReachExtender.HasValue))
            return null;

        var latest = ordered.LastOrDefault(p => p.PathLossDb.HasValue);
        var loss = latest?.PathLossDb;
        var launch = ordered.LastOrDefault(p => p.OltLaunchPowerDbm.HasValue)?.OltLaunchPowerDbm;
        var cls = ordered.LastOrDefault(p => p.OdnClass.HasValue)?.OdnClass;
        var budget = cls is >= 0 and <= 4 ? OdnClasses[cls.Value] : ((string Name, double MinDb, double MaxDb)?)null;

        double? margin = loss.HasValue && budget.HasValue ? Math.Round(budget.Value.MaxDb - loss.Value, 2) : null;
        double? belowMin = loss.HasValue && budget.HasValue && loss.Value < budget.Value.MinDb
            ? Math.Round(budget.Value.MinDb - loss.Value, 2) : null;

        // Loss and launch power of the points that carry both, so the two changes and the RX
        // change they imply describe the same samples.
        var paired = ordered.Where(p => p.PathLossDb.HasValue && p.OltLaunchPowerDbm.HasValue).ToList();
        double? baseline = null, lossChange = null, launchChange = null, rxChange = null;
        if (paired.Count >= RecentPoints + MinBaselinePoints)
        {
            var recent = paired.Skip(paired.Count - RecentPoints).ToList();
            var earlier = paired.Take(paired.Count - RecentPoints).ToList();
            baseline = Math.Round(Median(earlier.Select(p => p.PathLossDb!.Value)), 2);
            lossChange = Math.Round(Median(recent.Select(p => p.PathLossDb!.Value)) - baseline.Value, 2);
            launchChange = Math.Round(
                Median(recent.Select(p => p.OltLaunchPowerDbm!.Value)) - Median(earlier.Select(p => p.OltLaunchPowerDbm!.Value)), 2);
            rxChange = Math.Round(launchChange.Value - lossChange.Value, 2);
        }

        var hints = new List<string>();
        if (margin < 0) hints.Add("over_limit");
        else if (margin < PathLossNearLimitDb) hints.Add("near_limit");
        if (belowMin.HasValue) hints.Add("below_min");
        if (lossChange >= PathLossRiseHintDb) hints.Add("loss_rise");
        if (launchChange <= -LaunchDropHintDb) hints.Add("launch_drop");

        return new PathLossSummary
        {
            LossDb = loss,
            LaunchDbm = launch,
            RxDbm = latest?.OltLaunchPowerDbm is double l && loss.HasValue ? Math.Round(l - loss.Value, 2) : null,
            ReachExtender = ordered.LastOrDefault(p => p.ReachExtender.HasValue)?.ReachExtender == 1,
            OdnClass = budget?.Name,
            ClassMinDb = budget?.MinDb,
            ClassMaxDb = budget?.MaxDb,
            MarginDb = margin,
            BelowMinDb = belowMin,
            BaselineLossDb = baseline,
            LossChangeDb = lossChange,
            LaunchChangeDb = launchChange,
            RxChangeDb = rxChange,
            Level = hints.Contains("over_limit") ? "warning" : hints.Count > 0 ? "notice" : "ok",
            Hints = hints,
            Changes = PonIdChanges(ordered),
        };
    }

    /// <summary>
    /// Every point where the OLT's announcement differs from the last one seen: class, reach
    /// extender, launch power. A point without the value is a missed read, not a change. The
    /// launch power is a measurement on OLTs that report one and wanders by a 0.1 dB step or two,
    /// so it counts as changed only once it stands a full <see cref="LaunchDropHintDb"/> away from
    /// the level last recorded; a slow drift is recorded when it has added up to that. The newest
    /// few, oldest first.
    /// </summary>
    private static List<PonIdChange> PonIdChanges(List<MonitoringInfluxClient.PonSeriesPoint> ordered)
    {
        static string ClassName(long c) => c is >= 0 and <= 4 ? OdnClasses[c].Name : c.ToString(CultureInfo.InvariantCulture);
        static string Dbm(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dBm";

        var changes = new List<PonIdChange>();
        long? cls = null, ext = null;
        double? launch = null;
        foreach (var p in ordered)
        {
            var time = p.Time.ToString("o");
            if (p.OdnClass is long c)
            {
                if (cls is long was && was != c) changes.Add(new(time, "odn_class", ClassName(was), ClassName(c)));
                cls = c;
            }
            if (p.ReachExtender is long e)
            {
                if (ext is long was && was != e) changes.Add(new(time, "reach_extender", was == 1 ? "yes" : "no", e == 1 ? "yes" : "no"));
                ext = e;
            }
            if (p.OltLaunchPowerDbm is double l)
            {
                if (launch is not double was) launch = l;
                else if (Math.Abs(was - l) >= LaunchDropHintDb - 1e-9)
                {
                    changes.Add(new(time, "launch_power", Dbm(was), Dbm(l)));
                    launch = l;
                }
            }
        }
        return changes.Count > MaxChanges ? changes.GetRange(changes.Count - MaxChanges, MaxChanges) : changes;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

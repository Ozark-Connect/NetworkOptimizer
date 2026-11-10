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
                dsOwnShare = OwnShare(p, prev),
                bias = p.BiasMa,
                hostLink = p.HostLinkUp,
                // What the ONT's own clocks say happened inside the interval.
                downS = SameBoot(p, prev) ? DeltaSeconds(p.NotO5Seconds, prev?.NotO5Seconds) : null,
                losS = SameBoot(p, prev) ? DeltaSeconds(p.LosSeconds, prev?.LosSeconds) : null,
                lofS = SameBoot(p, prev) ? DeltaSeconds(p.LofSeconds, prev?.LofSeconds) : null,
                // Upstream scheduling: what the OLT grants this ONT and what the ONT makes of it.
                grantsPerS = PerSecond(p.AllocTotal, prev?.AllocTotal, p, prev),
                burstsPerS = PerSecond(p.UsBursts, prev?.UsBursts, p, prev),
                dbruPerS = PerSecond(p.UsDbru, prev?.UsDbru, p, prev),
                bytesPerBurst = Delta(p.GemTxBytes, prev?.GemTxBytes) is long txBytes && Delta(p.UsBursts, prev?.UsBursts) is long bursts and > 0
                    ? Math.Round((double)txBytes / bursts, 1) : (double?)null,
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

        if (IntervalSeconds(cur, prev) is not double seconds) return null;

        return Math.Round((bytes - bytesPrev) * 8 / seconds / 1e6, 3);
    }

    /// <summary>The length of one interval: by the superframe counter where there is one, by the clock otherwise.</summary>
    private static double? IntervalSeconds(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint prev)
    {
        var seconds = FramesBetween(cur, prev) is long frames
            ? frames / GponFramesPerSecond
            : (cur.Time - prev.Time).TotalSeconds;
        return seconds > 0 ? seconds : null;
    }

    /// <summary>
    /// This ONT's share of everything the PON carried downstream in the interval, in percent.
    /// Both sides come from the same pair of points, so numerator and denominator cover the same
    /// stretch. Null without both, and null when the PON's own figure is zero.
    /// </summary>
    public static double? OwnShare(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint? prev)
    {
        if (OwnDownstreamMbps(cur, prev) is not double own
            || DownstreamUtilization(cur, prev) is not { Mbps: > 0 } load)
            return null;
        return Math.Round(Math.Min(100, own / load.Mbps * 100), 2);
    }

    /// <summary>
    /// False across a reboot of the ONT: its uptime went back, so every counter restarted and the
    /// interval says nothing about the line. Points without an uptime are taken as one boot.
    /// </summary>
    internal static bool SameBoot(
        MonitoringInfluxClient.PonSeriesPoint cur, MonitoringInfluxClient.PonSeriesPoint? prev) =>
        prev is not null && !(cur.SfpUptimeS is long up && prev.SfpUptimeS is long upPrev && up < upPrev);

    private static double? DeltaSeconds(double? cur, double? prev) =>
        cur is double c && prev is double p && c >= p ? Math.Round(c - p, 1) : null;

    private static double? PerSecond(
        long? cur, long? prev, MonitoringInfluxClient.PonSeriesPoint curPoint, MonitoringInfluxClient.PonSeriesPoint? prevPoint)
    {
        if (prevPoint is null || cur is not long c || prev is not long p || c < p
            || IntervalSeconds(curPoint, prevPoint) is not double seconds)
            return null;
        return Math.Round((c - p) / seconds, 1);
    }

    /// <summary>One stretch in which the PON link was down, by the cause its counters point at.</summary>
    public sealed record PonOutage(string Time, double DownSeconds, long Drops, string Cause);

    /// <summary>
    /// How available the PON link was over the window, from the ONT's own state clock rather than
    /// from the state at each poll, so a drop between two polls counts.
    /// </summary>
    public sealed record AvailabilitySummary
    {
        /// <summary>Seconds in O5 as a share of all seconds the ONT timed, in percent.</summary>
        public double? AvailabilityPercent { get; init; }
        public double DownSeconds { get; init; }
        /// <summary>Times the ONT left O5.</summary>
        public long Drops { get; init; }
        public long LosEvents { get; init; }
        public double LosSeconds { get; init; }
        public long LofEvents { get; init; }
        public long OltDeactivations { get; init; }
        /// <summary>Reboots of the ONT inside the window. Not counted as downtime of the line.</summary>
        public IReadOnlyList<PonReboot> Reboots { get; init; } = [];
        /// <summary>The newest stretches with a drop or downtime, oldest first.</summary>
        public IReadOnlyList<PonOutage> Outages { get; init; } = [];
    }

    public sealed record PonReboot(string Time, string? Reason);

    private const int MaxOutages = 10;

    /// <summary>How far back the hour-by-weekday load map reads, as an offset in days: four of each weekday.</summary>
    public const int LoadMapDays = -28;

    /// <summary>
    /// Null when no point carries the ONT's state clock. An interval across a reboot is left out of
    /// the sums and listed as a reboot: the counters restarted, and what the line did meanwhile is
    /// not known. The cause of an outage is read off what else moved in the same interval: a loss
    /// of signal or of frame is the light ("light"), a deactivation message is the OLT sending the
    /// ONT away ("olt"), both is "light+olt", neither is "unknown".
    /// </summary>
    public static AvailabilitySummary? SummarizeAvailability(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 0 } || !points.Any(p => p.O5Seconds.HasValue)) return null;
        var ordered = points.OrderBy(p => p.Time).ToList();

        double up = 0, down = 0, losSeconds = 0;
        long drops = 0, los = 0, lof = 0, deact = 0;
        var outages = new List<PonOutage>();
        var reboots = new List<PonReboot>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var (cur, prev) = (ordered[i], ordered[i - 1]);
            if (!SameBoot(cur, prev))
            {
                reboots.Add(new(cur.Time.ToString("o"), cur.LastResetReason));
                continue;
            }
            var dUp = DeltaSeconds(cur.O5Seconds, prev.O5Seconds) ?? 0;
            var dDown = DeltaSeconds(cur.NotO5Seconds, prev.NotO5Seconds) ?? 0;
            var dDrops = Delta(cur.O5Exits, prev.O5Exits) ?? 0;
            var dLos = Delta(cur.LosEvents, prev.LosEvents) ?? 0;
            var dLof = Delta(cur.LofEvents, prev.LofEvents) ?? 0;
            var dDeact = Delta(cur.OltDeactivations, prev.OltDeactivations) ?? 0;
            up += dUp;
            down += dDown;
            drops += dDrops;
            los += dLos;
            lof += dLof;
            deact += dDeact;
            losSeconds += DeltaSeconds(cur.LosSeconds, prev.LosSeconds) ?? 0;

            if (dDrops > 0 || dDown > 0)
            {
                var light = dLos > 0 || dLof > 0;
                var cause = (light, dDeact > 0) switch
                {
                    (true, true) => "light+olt",
                    (true, false) => "light",
                    (false, true) => "olt",
                    _ => "unknown",
                };
                outages.Add(new(cur.Time.ToString("o"), dDown, dDrops, cause));
            }
        }

        return new AvailabilitySummary
        {
            AvailabilityPercent = up + down > 0 ? Math.Round(up / (up + down) * 100, 4) : null,
            DownSeconds = Math.Round(down, 1),
            Drops = drops,
            LosEvents = los,
            LosSeconds = Math.Round(losSeconds, 1),
            LofEvents = lof,
            OltDeactivations = deact,
            Reboots = reboots.Count > MaxOutages ? reboots.GetRange(reboots.Count - MaxOutages, MaxOutages) : reboots,
            Outages = outages.Count > MaxOutages ? outages.GetRange(outages.Count - MaxOutages, MaxOutages) : outages,
        };
    }

    private static long? Delta(long? cur, long? prev) =>
        cur is long c && prev is long p && c >= p ? c - p : null;

    /// <summary>
    /// The PON's downstream load per point of an hourly series, for the hour-by-weekday map. The
    /// points are the last reading of each hour, so each value is that hour's mean.
    /// </summary>
    public static List<object> HourlyLoad(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        var items = new List<object>();
        if (points is not { Count: > 1 }) return items;
        var ordered = points.OrderBy(p => p.Time).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            // A gap in the data is not an hour: two points far apart would smear days into one cell.
            if ((ordered[i].Time - ordered[i - 1].Time).TotalHours > 1.5) continue;
            if (DownstreamUtilization(ordered[i], ordered[i - 1]) is not { } load) continue;
            // The middle of the hour the value stands for, so it lands in the right cell.
            var mid = ordered[i - 1].Time + (ordered[i].Time - ordered[i - 1].Time) / 2;
            items.Add(new { time = mid.ToString("o"), pct = load.Percent, mbps = load.Mbps, own = OwnDownstreamMbps(ordered[i], ordered[i - 1]) });
        }
        return items;
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
    /// <summary>A change this large from one point to the next is a jump, far outside the RX reading's noise.</summary>
    internal const double PathLossJumpDb = 0.7;
    /// <summary>This many jumps in one window is the loss going out and back at least twice.</summary>
    internal const int PathLossUnsteadyJumps = 4;
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
        /// <summary>
        /// How far the loss ranges in the window: the span between its 5th and 95th percentile.
        /// </summary>
        public double? SpreadDb { get; init; }
        /// <summary>
        /// Jumps of <see cref="PathLossJumpDb"/> or more from one point to the next. A steady fibre
        /// has none and a step to a new level is one; several are the loss going back and forth,
        /// which is what a loose or dirty connector does.
        /// </summary>
        public int Jumps { get; init; }
        /// <summary>"ok", "notice" or "warning": the worst of the hints below.</summary>
        public string Level { get; init; } = "ok";
        /// <summary>Stable keys of what applies: over_limit, near_limit, below_min, loss_rise, launch_drop, unsteady.</summary>
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

        double? spread = null;
        var inOrder = ordered.Where(p => p.PathLossDb.HasValue).Select(p => p.PathLossDb!.Value).ToList();
        var losses = inOrder.OrderBy(v => v).ToList();
        if (losses.Count >= RecentPoints + MinBaselinePoints)
            spread = Math.Round(losses[(int)Math.Floor((losses.Count - 1) * 0.95)] - losses[(int)Math.Ceiling((losses.Count - 1) * 0.05)], 2);
        var jumps = 0;
        for (var i = 1; i < inOrder.Count; i++)
            if (Math.Abs(inOrder[i] - inOrder[i - 1]) >= PathLossJumpDb - 1e-9) jumps++;

        var hints = new List<string>();
        if (margin < 0) hints.Add("over_limit");
        else if (margin < PathLossNearLimitDb) hints.Add("near_limit");
        if (belowMin.HasValue) hints.Add("below_min");
        if (lossChange >= PathLossRiseHintDb) hints.Add("loss_rise");
        if (launchChange <= -LaunchDropHintDb) hints.Add("launch_drop");
        // One jump is a step to a new level, named above if it is large; this is back and forth.
        if (jumps >= PathLossUnsteadyJumps) hints.Add("unsteady");

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
            SpreadDb = spread,
            Jumps = jumps,
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

    // ---------------------------------------------------------------- further read-outs

    /// <summary>One step of the connection check: "ok", "fail" or "unknown", and what it was read from.</summary>
    public sealed record CheckStep(string Key, string Status, string Detail);

    /// <summary>
    /// The link from the fibre up to traffic in five steps, each read from the latest interval:
    /// light (the path loss inside the window of the announced ODN class), registered (O5 and
    /// being granted upstream), management channel (the OMCI daemon answers), service (the OLT
    /// has provisioned at least one) and traffic (bytes moved on the ONT's GEM ports). A step
    /// whose source is missing is "unknown", never "ok". Null when the ONT reports no state.
    /// </summary>
    public static List<CheckStep>? ConnectionCheck(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 0 }) return null;
        var ordered = points.OrderBy(p => p.Time).ToList();
        var cur = ordered[^1];
        var prev = ordered.Count > 1 ? ordered[^2] : null;
        if (cur.PonLinkStatus is null && cur.OmciUp is null) return null;
        var inv = CultureInfo.InvariantCulture;

        CheckStep Light()
        {
            if (cur.PathLossDb is not double loss || cur.OdnClass is not (>= 0 and <= 4))
                return new("light", "unknown", "the OLT announces no launch power or class");
            var (name, min, max) = OdnClasses[cur.OdnClass.Value];
            var ok = loss >= min && loss <= max;
            return new("light", ok ? "ok" : "fail",
                string.Create(inv, $"path loss {loss:0.0} dB, class {name} allows {min:0} to {max:0} dB"));
        }

        CheckStep Registered()
        {
            if (cur.PonLinkStatus is null) return new("registered", "unknown", "no ONU state reported");
            if (cur.PonLinkStatus != "operation") return new("registered", "fail", $"ONU state is {cur.PonLinkStatus}, not O5");
            var grants = prev is null ? null : PerSecond(cur.AllocTotal, prev.AllocTotal, cur, prev);
            return grants switch
            {
                null => new("registered", "ok", "in O5"),
                0 => new("registered", "fail", "in O5, but the OLT grants this ONT nothing"),
                _ => new("registered", "ok", string.Create(inv, $"in O5, {grants:0} grants per second")),
            };
        }

        CheckStep Management() => cur.OmciUp switch
        {
            1 => new("management", "ok", "the OMCI daemon answers"),
            0 => new("management", "fail", "the OMCI daemon did not answer"),
            _ => new("management", "unknown", "not reported"),
        };

        CheckStep Service() => cur.OmciServices switch
        {
            null => new("service", "unknown", "not reported"),
            0 => new("service", "fail", "the OLT has provisioned no service"),
            var n => new("service", "ok", string.Create(inv, $"{n} provisioned")),
        };

        CheckStep Traffic()
        {
            var down = prev is null ? null : Delta(cur.GemRxBytes, prev.GemRxBytes);
            var up = prev is null ? null : Delta(cur.GemTxBytes, prev.GemTxBytes);
            if (down is null && up is null) return new("traffic", "unknown", "no byte counters for this interval");
            return (down ?? 0) > 0 || (up ?? 0) > 0
                ? new("traffic", "ok", "bytes moved in the last interval")
                : new("traffic", "fail", "no bytes moved in the last interval");
        }

        return [Light(), Registered(), Management(), Service(), Traffic()];
    }

    /// <summary>How much the forward error correction had to do over the window, and whether anything got past it.</summary>
    public sealed record FecSummary
    {
        public long CorrectedCodewords { get; init; }
        public long UncorrectableCodewords { get; init; }
        public long? CorrectedBits { get; init; }
        public long BipErrors { get; init; }
        /// <summary>Corrected bits over all bits received in the same intervals: the line's error ratio before FEC.</summary>
        public double? PreFecBitErrorRatio { get; init; }
        /// <summary>"ok" (nothing to correct), "notice" (corrections, none lost) or "warning" (uncorrectable codewords: data was lost).</summary>
        public string Level { get; init; } = "ok";
    }

    /// <summary>Null when no point carries a FEC counter: FEC off, or a source without them.</summary>
    public static FecSummary? SummarizeFec(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 1 } || !points.Any(p => p.FecCorrectedWords.HasValue || p.FecCorrectedBits.HasValue))
            return null;
        var ordered = points.OrderBy(p => p.Time).ToList();
        long corrected = 0, uncorrectable = 0, bits = 0, bip = 0, frames = 0;
        var anyBits = false;
        for (var i = 1; i < ordered.Count; i++)
        {
            var (cur, prev) = (ordered[i], ordered[i - 1]);
            corrected += Delta(cur.FecCorrectedWords, prev.FecCorrectedWords) ?? 0;
            uncorrectable += Delta(cur.FecErrors, prev.FecErrors) ?? 0;
            bip += Delta(cur.BipErrors, prev.BipErrors) ?? 0;
            // Bits and the frames they were found in from the same intervals, or the ratio is skewed.
            if (Delta(cur.FecCorrectedBits, prev.FecCorrectedBits) is long b && FramesBetween(cur, prev) is long f)
            {
                bits += b;
                frames += f;
                anyBits = true;
            }
        }
        return new FecSummary
        {
            CorrectedCodewords = corrected,
            UncorrectableCodewords = uncorrectable,
            CorrectedBits = anyBits ? bits : null,
            BipErrors = bip,
            PreFecBitErrorRatio = anyBits && frames > 0 ? bits / (frames * GponFrameBytes * 8) : null,
            Level = uncorrectable > 0 ? "warning" : corrected > 0 || bits > 0 ? "notice" : "ok",
        };
    }

    /// <summary>The transmit laser's bias current over the window, against its own limit and against itself.</summary>
    public sealed record LaserSummary
    {
        public double BiasMa { get; init; }
        public double? WarnHighMa { get; init; }
        /// <summary>Later third of the window against the first third, in percent of the earlier level.</summary>
        public double? BiasChangePercent { get; init; }
        public double? TxPowerChangeDb { get; init; }
        public double? TemperatureChangeC { get; init; }
        /// <summary>The module's own alarm and warning flags that are raised now.</summary>
        public IReadOnlyList<string> ModuleAlarms { get; init; } = [];
        /// <summary>Stable keys: bias_rise (more current for the same light at the same temperature), near_limit, module_alarm.</summary>
        public IReadOnlyList<string> Hints { get; init; } = [];
    }

    /// <summary>More bias current than this for the same light is worth a line.</summary>
    internal const double BiasRiseHintPercent = 10;

    /// <summary>
    /// Null when no point carries a bias current. A laser that ages needs more current for the
    /// same light, so the rise only counts while TX power and temperature stayed where they were:
    /// a warmer module or a higher set power draws more current with nothing wrong.
    /// </summary>
    public static LaserSummary? SummarizeLaser(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        var withBias = points?.Where(p => p.BiasMa.HasValue).OrderBy(p => p.Time).ToList();
        if (withBias is not { Count: > 0 }) return null;
        var latest = withBias[^1];

        double? biasChange = null, txChange = null, tempChange = null;
        var third = withBias.Count / 3;
        if (third >= 3)
        {
            var early = withBias.Take(third).ToList();
            var late = withBias.Skip(withBias.Count - third).ToList();
            var earlyBias = Median(early.Select(p => p.BiasMa!.Value));
            if (earlyBias > 0)
                biasChange = Math.Round((Median(late.Select(p => p.BiasMa!.Value)) - earlyBias) / earlyBias * 100, 1);
            if (early.All(p => p.OntTxPowerDbm.HasValue) && late.All(p => p.OntTxPowerDbm.HasValue))
                txChange = Math.Round(Median(late.Select(p => p.OntTxPowerDbm!.Value)) - Median(early.Select(p => p.OntTxPowerDbm!.Value)), 2);
            if (early.All(p => p.OntTemperatureC.HasValue) && late.All(p => p.OntTemperatureC.HasValue))
                tempChange = Math.Round(Median(late.Select(p => p.OntTemperatureC!.Value)) - Median(early.Select(p => p.OntTemperatureC!.Value)), 1);
        }

        var alarms = string.IsNullOrEmpty(latest.TransceiverAlarms)
            ? new List<string>()
            : latest.TransceiverAlarms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var hints = new List<string>();
        if (biasChange >= BiasRiseHintPercent && Math.Abs(txChange ?? double.MaxValue) < 0.5 && Math.Abs(tempChange ?? double.MaxValue) < 3)
            hints.Add("bias_rise");
        if (latest.BiasWarnHighMa is double limit && latest.BiasMa!.Value >= limit * 0.8) hints.Add("near_limit");
        if (alarms.Count > 0) hints.Add("module_alarm");

        return new LaserSummary
        {
            BiasMa = latest.BiasMa!.Value,
            WarnHighMa = latest.BiasWarnHighMa,
            BiasChangePercent = biasChange,
            TxPowerChangeDb = txChange,
            TemperatureChangeC = tempChange,
            ModuleAlarms = alarms,
            Hints = hints,
        };
    }

    /// <summary>What the provider's side did to this ONT in the window, as the ONT's management counters show it.</summary>
    public sealed record ProviderSummary
    {
        /// <summary>Downstream OMCI messages per minute over the latest interval: the OLT's polling pace.</summary>
        public double? OmciPerMinute { get; init; }
        public long? Services { get; init; }
        /// <summary>The traffic descriptor's peak and committed rate in Mbit/s.</summary>
        public double? PirMbps { get; init; }
        public double? CirMbps { get; init; }
        /// <summary>The newest events, oldest first: config_written, key_renewed, reranged, rate_changed, services_changed.</summary>
        public IReadOnlyList<PonIdChange> Events { get; init; } = [];
    }

    /// <summary>
    /// Null when no point carries any of it. Events are read only between two points of one boot:
    /// after a restart the OLT provisions the ONT from nothing, which is not news. The first key
    /// and the first ranging belong to activation, so only a count that rises afterwards is one.
    /// </summary>
    public static ProviderSummary? SummarizeProvider(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        if (points is not { Count: > 0 }
            || !points.Any(p => p.OmciRxFrames.HasValue || p.MibDataSync.HasValue || p.TrafficPirBytesPerSecond.HasValue))
            return null;
        var ordered = points.OrderBy(p => p.Time).ToList();
        var inv = CultureInfo.InvariantCulture;
        static double Mbps(long bytesPerSecond) => Math.Round(bytesPerSecond * 8 / 1e6, 1);
        string Rate(long? v) => v is long b ? string.Create(inv, $"{Mbps(b):0.#} Mbit/s") : "none";

        var events = new List<PonIdChange>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var (cur, prev) = (ordered[i], ordered[i - 1]);
            if (!SameBoot(cur, prev)) continue;
            var time = cur.Time.ToString("o");
            if (cur.MibDataSync is long sync && prev.MibDataSync is long syncPrev && sync != syncPrev)
                events.Add(new(time, "config_written", syncPrev.ToString(inv), sync.ToString(inv)));
            // A drop is followed by a new activation, with its own ranging and key: part of coming
            // back, and already told by the availability read-out.
            var stayedUp = (Delta(cur.O5Exits, prev.O5Exits) ?? 0) == 0;
            if (stayedUp && (Delta(cur.PloamKeySwitch, prev.PloamKeySwitch) is > 0 || Delta(cur.PloamRequestKey, prev.PloamRequestKey) is > 0))
                events.Add(new(time, "key_renewed", (prev.PloamKeySwitch ?? 0).ToString(inv), (cur.PloamKeySwitch ?? 0).ToString(inv)));
            if (stayedUp && Delta(cur.PloamRangingTime, prev.PloamRangingTime) is > 0)
                events.Add(new(time, "reranged", prev.PloamRangingTime!.Value.ToString(inv), cur.PloamRangingTime!.Value.ToString(inv)));
            if (cur.TrafficPirBytesPerSecond != prev.TrafficPirBytesPerSecond
                && (cur.TrafficPirBytesPerSecond.HasValue || prev.TrafficPirBytesPerSecond.HasValue)
                && cur.OmciUp != 0 && prev.OmciUp != 0)
                events.Add(new(time, "rate_changed", Rate(prev.TrafficPirBytesPerSecond), Rate(cur.TrafficPirBytesPerSecond)));
            if (cur.OmciServices is long svc && prev.OmciServices is long svcPrev && svc != svcPrev)
                events.Add(new(time, "services_changed", svcPrev.ToString(inv), svc.ToString(inv)));
        }

        var last = ordered[^1];
        var before = ordered.Count > 1 ? ordered[^2] : null;
        var omciRate = before is not null && SameBoot(last, before)
            ? PerSecond(last.OmciRxFrames, before.OmciRxFrames, last, before) : null;
        var pir = ordered.LastOrDefault(p => p.TrafficPirBytesPerSecond.HasValue)?.TrafficPirBytesPerSecond;
        var cir = ordered.LastOrDefault(p => p.TrafficCirBytesPerSecond.HasValue)?.TrafficCirBytesPerSecond;
        return new ProviderSummary
        {
            OmciPerMinute = omciRate is double r ? Math.Round(r * 60, 1) : null,
            Services = ordered.LastOrDefault(p => p.OmciServices.HasValue)?.OmciServices,
            PirMbps = pir is long p1 ? Mbps(p1) : null,
            CirMbps = cir is long c1 ? Mbps(c1) : null,
            Events = events.Count > MaxChanges ? events.GetRange(events.Count - MaxChanges, MaxChanges) : events,
        };
    }

    /// <summary>The read-outs beyond the charts, bundled so both chart endpoints attach them with one call.</summary>
    public static object? Insights(List<MonitoringInfluxClient.PonSeriesPoint>? points)
    {
        var check = ConnectionCheck(points);
        var fec = SummarizeFec(points);
        var laser = SummarizeLaser(points);
        var provider = SummarizeProvider(points);
        return check is null && fec is null && laser is null && provider is null
            ? null
            : new { check, fec, laser, provider };
    }
}

using System.Globalization;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Pure scoring engine for ISP Health. Takes pre-assembled inputs (latency series,
/// throughput, detected events) plus an access technology profile and produces the
/// full report. No I/O; fully unit-testable. Formulas and anchor points are tuned
/// against real incident data.
/// </summary>
public class IspHealthScorer
{
    private readonly IspHealthOptions _options;
    private readonly ILogger? _logger;

    // Outage windows for the current report. An outage's only score impact is the single
    // capped Packet Loss penalty, so its near-total-loss samples are excluded from every
    // other loss aggregation (per-ASN/hop grades, loaded loss, displayed loss) - otherwise
    // they would double-count and tank the Transit/ISP dimensions. Set per Score() call.
    private IReadOnlyList<OutageEvent> _outages = System.Array.Empty<OutageEvent>();

    // The access profile for the current report, set per Score() call. Carries the per-tech jitter
    // band (Item E) used to grade ISP and transit jitter against the access medium's inherent floor.
    private AccessProfile? _profile;

    // Only blackout outages mask their loss from the other factors; partial-loss disruptions are
    // deliberately left in the Packet Loss factor (their loss IS the degradation signal) - unless
    // the user marked one "that was me", where the whole span is their own doing and must not
    // leak into the loss factors either.
    private bool InOutage(DateTime time) => _outages.Any(o => (!o.IsPartial || o.Acknowledged) && time >= o.Start && time < o.End);

    public IspHealthScorer(IspHealthOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public IspHealthReport Score(IspHealthInputs inputs, AccessProfile profile)
    {
        _outages = inputs.Outages;
        _profile = profile;
        _loadedDownKeys = null;
        _loadedUpKeys = null;
        if (inputs.LoadExclusionWindows.Count > 0)
        {
            foreach (var (exStart, exEnd) in inputs.LoadExclusionWindows)
                _logger?.LogDebug("ISP Health: excluding SQM probe window {Start} to {End}", exStart.ToString("u"), exEnd.ToString("u"));
        }
        var loadWindows = LoadClassifier.Classify(inputs.WanRates, inputs.ExpectedDownloadMbps, inputs.ExpectedUploadMbps, _options, inputs.LoadExclusionWindows, _logger);
        var hasExpectedSpeeds = inputs.ExpectedDownloadMbps.HasValue || inputs.ExpectedUploadMbps.HasValue;

        var idleBaseline = ComputeIdleBaseline(inputs.FirstHopSeries, loadWindows);
        var avgLoad = ComputeAverageLoad(inputs);
        var (speedVsPlan, bestSpeedTest, typicalDownMbps, typicalUpMbps) = ScoreSpeedVsPlan(inputs);
        var idleLatency = ScoreIdleLatency(idleBaseline, profile);
        var packetLoss = ScorePacketLoss(inputs.LossPoolSeries, profile, avgLoad);
        var loadedDeltas = ResolveLoadedDeltas(inputs, loadWindows);

        // The path jitter floor: the quietest median jitter measured anywhere along the
        // path (ISP hops and transit clusters). It represents the access layer's inherent
        // stability - every probe crosses it - so jitter is graded relative to this floor.
        var jitterFloor = ComputeJitterFloor(inputs);
        _logger?.LogDebug("ISP Health: path jitter floor {Floor} ms", FormatMsOrNull(jitterFloor));
        var (loadedLatency, hasLoadedLatency) = ScoreLoadedLatency(loadedDeltas, profile);
        var (loadedLoss, hasLoadedLoss) = ScoreLoadedLoss(inputs.LossPoolSeries, loadWindows, profile);

        // Physical Link: the access medium's own physical layer (optical RX, DOCSIS RF/FEC,
        // cellular signal). Null factor (omitted, no penalty) when no source matched the WAN.
        var physicalIssues = new List<IspHealthIssue>();
        IspScoreFactor physicalLink;
        if (inputs.PhysicalLink is not null)
        {
            var physical = PhysicalLinkScorer.Score(inputs.PhysicalLink, inputs.ExpectedUploadMbps, _options.PhysicalLinkWeight, _logger);
            physicalLink = physical.Factor;
            physicalIssues = physical.Issues;
            _logger?.LogDebug("ISP Health physical link factor: {Medium} '{Source}' -> {Score} ({Value})",
                inputs.PhysicalLink.Medium, inputs.PhysicalLink.SourceName, physicalLink.Score, physicalLink.ValueText ?? "n/a");
        }
        else
        {
            physicalLink = new IspScoreFactor
            {
                Name = "Physical Link",
                Score = null,
                Weight = _options.PhysicalLinkWeight,
                Description = "No monitored access-link device (ONT, cable modem, or cellular modem) matched this WAN."
            };
        }

        var accessFactors = new List<IspScoreFactor> { speedVsPlan, idleLatency, packetLoss, loadedLatency, loadedLoss, physicalLink };
        var accessDimension = BuildDimension("Access Layer", _options.AccessWeight, accessFactors);

        var accessMedianRtt = SeriesStats.Median(
            inputs.FirstHopSeries.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList());

        // Absolution witnesses = internet destinations plus witness-only Custom targets. Peering
        // selection and involvement counts below use inputs.DestinationSeries directly (internet
        // only), so a witness-only CMTS/PoP never counts as an internet destination.
        var witnessDestinations = inputs.DestinationSeries.Concat(inputs.WitnessSeries).ToList();

        var transitAsns = GradeTransitAsns(inputs.TransitAsnSeries, witnessDestinations, inputs.HopOrderKnown,
            inputs.CongestionEvents, jitterFloor, accessMedianRtt, inputs.InternetMedianDeltaMs);

        // IX Peering: destinations reached over the access ISP's own peering/IX (see
        // IxPeeringMaxBestCaseDeltaMs) are graded end-to-end and inserted as one synthetic transit
        // entry, so the REAL measured peering quality carries Transit Health instead of the neutral-100
        // fill an otherwise-empty (purely peered) transit dimension would average against. Nothing
        // qualifies where every destination crosses a transit provider (e.g. a rural backhaul), and no
        // entry is added - the prior behavior stands.
        var peeringReached = SelectPeeringReachedDestinations(inputs);
        if (peeringReached.Count > 0)
        {
            // Median RTT of the real transit ASNs (before the IX entry joins the list). Peering that
            // delivers the internet far closer than this earns back some of its jitter - see GradeIxPeering.
            var transitReferenceRtt = SeriesStats.Median(
                transitAsns.Where(a => a.MeanRttMs.HasValue).Select(a => a.MeanRttMs!.Value).ToList());
            var ixGrade = GradeIxPeering(peeringReached, inputs.CongestionEvents, jitterFloor, accessMedianRtt,
                inputs.InternetMedianDeltaMs, transitReferenceRtt);
            transitAsns.Insert(0, ixGrade);
            _logger?.LogDebug(
                "ISP Health: IX Peering entry from {N} direct-peered destination(s) [{Targets}] -> grade {Grade}",
                peeringReached.Count, string.Join(", ", peeringReached.Select(d => d.AsnName)), ixGrade.OverallScore);
        }

        // Arm 4: each transit ASN's "internet host involvement" - how many monitored internet
        // destinations are proven to route through it (routes-through gated on stored ancestry).
        // Feeds the involvement-weighted Transit dimension below. Zero for every ASN when transit
        // is traceroute-invisible on the destination paths, in which case the dimension falls back
        // to an equal-weighted average (the prior behavior).
        var transitHopIpsByAsn = inputs.TransitAsnSeries
            .GroupBy(s => s.AsnNumber)
            .ToDictionary(g => g.Key, g => g.SelectMany(s => s.HopIps).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var transitReachByAsn = transitHopIpsByAsn.ToDictionary(
            kv => kv.Key,
            kv => inputs.HopOrderKnown ? inputs.DestinationSeries.Count(d => RoutesThrough(d.AncestorIps, kv.Value)) : 0);
        // Reach drives the involvement weight. Real transit ASNs use the routes-through count above;
        // the synthetic IX Peering entry carries exactly the direct-peered destinations it was built
        // from, so in a peering-dominant path it holds max reach (weight ~1) and its real grade - not
        // the neutral 100 - carries the dimension.
        int ReachOf(IspAsnHealth a) => a.AsnNumber == IxPeeringAsn
            ? peeringReached.Count
            : transitReachByAsn.TryGetValue(a.AsnNumber, out var rc) ? rc : 0;
        var transitMaxReach = transitAsns.Count > 0 ? transitAsns.Max(ReachOf) : 0;

        // Attribution is "known" when we have the ancestry to prove routes-through AND destinations to
        // test against. Then reach == 0 is a TRUE zero (a peered site whose destinations cross no
        // transit) and floors the ASN at 25% - distinct from "no ancestry at all", where we can't tell
        // and leave involvement unset (equal weight). Attach the involvement to each transit ASN so
        // both Transit Health and the Networks on Your Path card read from one source.
        var transitAttributionKnown = inputs.HopOrderKnown && inputs.DestinationSeries.Count > 0;
        foreach (var a in transitAsns)
        {
            a.ShowInvolvement = transitAttributionKnown;
            a.InvolvementHostTotal = inputs.DestinationSeries.Count;
            a.InvolvementReach = ReachOf(a);
            a.InvolvementWeight = transitAttributionKnown
                ? (transitMaxReach > 0
                    ? TransitInvolvementFloor + (1 - TransitInvolvementFloor) * ((double)a.InvolvementReach / transitMaxReach)
                    : TransitInvolvementFloor)
                : null;
        }

        // Display order for both the Transit Health factor list and the Networks on Your Path card:
        // the IX Peering entry and the transit ASNs sorted by involvement weight descending (the
        // networks carrying most of your traffic first), the nearer network breaking ties. The Access
        // dimension renders separately and always leads the Networks card.
        transitAsns = transitAsns
            .OrderByDescending(a => a.InvolvementWeight ?? 0.0)
            .ThenBy(a => a.MeanRttMs ?? double.MaxValue)
            .ToList();

        // Every ISP hop is graded; the dimension averages them all. Each hop's jitter is
        // absolved per-hop and routes-through-gated (a transit ASN or deeper ISP hop only
        // absolves a hop it is proven downstream of), so a divergent clean transit can't
        // clear a congested hop it never traverses. Hops further out on the same ISP also
        // get a soft intra-ASN reach ceiling. Access layer idle latency still uses FirstHopSeries.
        var ispHopGrades = GradeIspHops(inputs.IspAsnSeries, inputs.TransitAsnSeries, transitAsns, witnessDestinations, inputs.CongestionEvents, jitterFloor, inputs.HopOrderKnown, accessMedianRtt, inputs.InternetMedianDeltaMs);
        // Collapse the per-hop grades to one entry per ASN for the Networks on Your Path card.
        var ispAsns = AggregateIspAsns(ispHopGrades, inputs.CongestionEvents, _options.JitterAssimilationMinDeltaMs);
        var transitDimension = BuildAsnDimension("Transit Health", _options.TransitWeight, transitAsns);
        var ispAsnDimension = BuildIspDimension(_options.IspAsnWeight, ispHopGrades);

        var overall = CombineDimensions(accessDimension, transitDimension, ispAsnDimension);
        // Outages are scored once, here at the top level (not inside a factor, where the dimension
        // weights would dilute a multi-hour outage to a couple of points), as TWO components:
        //   - DURATION: the GREATER of (a) the summed effective downtime as a PERCENT OF THE WINDOW,
        //     on the unavailability curve, and (b) what the single worst event earns on the felt-event
        //     curve from its absolute minutes. (a) makes the penalty mean an availability figure, so
        //     15 min costs what four-nines is worth on a 30-day window and what two-nines is worth on
        //     48 h; (b) stops a long outage dissolving into a long window, because four hours down is
        //     memorable no matter how much clean time surrounds it. A partial-loss disruption's
        //     minutes are weighted by its peak loss fraction (and a tunable weight) into both, so a
        //     shallow degradation dings less than a blackout of the same length.
        //   - OCCURRENCE: severity-weighted events per DAY on the occurrence rate curve. This is what
        //     makes recurrence bite - ten separate micro-drops cost far more than a single one, where
        //     the duration terms treat them as one slightly-longer drop. Scoring it as a rate (rather
        //     than the old per-event cost scaled by window ratio) means a given drop rate costs the
        //     same however long you look, instead of a steady rate looking BETTER on a longer window.
        // Local (LAN/gateway) outages are surfaced but never penalize the ISP - the gateway being
        // unreachable is the user's own LAN, not the ISP's fault (they still mask their dark window
        // from the other factors via InOutage, so that loss isn't double-counted against the ISP).
        // Both components are scaled by the event's time-of-day usage weight (1.0 unless the service
        // set it): an outage during the user's heavy-usage hours counts in full, one during typically
        // idle hours dings less.
        // Acknowledged ("that was me") outages are the user's own maintenance, not the ISP:
        // excluded from the penalty like Local ones, while their dark windows still mask loss.
        var wanOutages = inputs.Outages.Where(o => o.Scope != OutageScope.Local && !o.Acknowledged).ToList();
        // Severity factor 0..1 = breadth (fraction of monitored targets that dropped) x depth (peak
        // loss fraction), with a partial-loss disruption additionally scaled by its tunable weight.
        // BOTH fall back to 1.0 rather than 0 when the event carries no census or no recorded peak
        // loss. The detector only declares an outage past OutageMinReportingTargets and (for a
        // blackout) past OutageDarkLossPct, so an absent field is missing metadata, not a lossless
        // outage that touched nothing - and since this factor now scales effective MINUTES, reading
        // it as zero would let a real outage score completely free.
        double SeverityFactor(OutageEvent o)
        {
            var depth = o.PeakLossPct > 0 ? Math.Clamp(o.PeakLossPct / 100.0, 0, 1) : 1.0;
            var breadth = o.PathTargetCount > 0
                ? Math.Clamp((double)o.DegradedTargetCount / o.PathTargetCount, 0, 1)
                : 1.0;
            return depth * breadth * (o.IsPartial ? _options.OutagePartialPenaltyWeight : 1.0);
        }
        // Minutes of this event that actually fall INSIDE the window. Outage detection reaches back
        // OutageDetectionLeadInHours so an outage straddling the window start is stitched whole, and
        // such an event keeps its true onset (IspHealthService only drops ones that ended before the
        // start). Since downtime is now graded as a fraction of the window, the pre-window part must
        // not count against it - otherwise a window catching the last five minutes of a two-hour
        // outage reads as two hours down. o.Duration stays the true shape for the timeline and the
        // findings, which is what it is for.
        double InWindowMinutes(OutageEvent o)
        {
            var start = o.Start > inputs.WindowStart ? o.Start : inputs.WindowStart;
            var end = o.End < inputs.WindowEnd ? o.End : inputs.WindowEnd;
            return end > start ? (end - start).TotalMinutes : 0.0;
        }
        // Effective minutes: in-window duration discounted by how much of the path actually went
        // dark, how deep it went, and how much the line is typically used at that hour. An event that
        // took two of nine targets half-lossy did not cost you its full wall-clock minutes.
        double EffectiveMinutes(OutageEvent o) => InWindowMinutes(o) * SeverityFactor(o) * o.UsageWeight;
        // Severity 0..1 for the occurrence rate. A widespread near-total event at a busy hour reads hottest.
        double Severity(OutageEvent o) => SeverityFactor(o) * o.UsageWeight;
        var windowMinutes = (inputs.WindowEnd - inputs.WindowStart).TotalMinutes;
        // Percent of the window one event's effective (loss- and usage-weighted) minutes represent.
        double DownPercent(double effectiveMinutes) => windowMinutes > 0 ? 100.0 * effectiveMinutes / windowMinutes : 0.0;
        // What one event claims on its own: the greater of its ratio cost and its felt cost. Used both
        // as the worst-event floor below and, normalized, to attribute the duration penalty per event.
        double Claim(OutageEvent o)
        {
            var em = EffectiveMinutes(o);
            return Math.Max(
                ScoreCurve.Interpolate(DownPercent(em), _options.OutageUnavailabilityCurve),
                ScoreCurve.Interpolate(em, _options.OutageFeltEventCurve));
        }
        if (wanOutages.Count > 0)
        {
            var outageMinutes = wanOutages.Sum(EffectiveMinutes);
            var downPercent = DownPercent(outageMinutes);
            var unavailabilityPenalty = ScoreCurve.Interpolate(downPercent, _options.OutageUnavailabilityCurve);
            // The floor is the WORST single event, never the sum of every event's felt cost - summing
            // would double-count against the ratio term, which already has all of their minutes.
            var feltFloor = wanOutages.Max(Claim);
            var durationPenalty = Math.Max(unavailabilityPenalty, feltFloor);
            var windowDays = windowMinutes / 1440.0;
            var severityTotal = wanOutages.Sum(Severity);
            // Recurrence starts at the SECOND event, so the rate is built from everything past the
            // worst one. A lone outage is an incident, not a pattern - rating one event in 48 h as
            // "0.5 a day" would read a single sample as a trend and charge it twice, once here and
            // once on duration. Its cost belongs entirely to the duration terms.
            var recurringSeverity = Math.Max(0, severityTotal - wanOutages.Max(Severity));
            var eventsPerDay = windowDays > 0 ? recurringSeverity / windowDays : 0.0;
            var occurrencePenalty = ScoreCurve.Interpolate(eventsPerDay, _options.OutageOccurrenceRateCurve);
            // Floor a flagged WAN event at one point: if we surfaced it on the timeline it should
            // visibly register, not round to a silent zero.
            var penalty = Math.Max(1.0, durationPenalty + occurrencePenalty);
            // Attribute the total across events so each row shows its own "-N points". Duration goes by
            // each event's share of the summed claim, which tracks whichever term won: when one long
            // outage set the floor its claim dominates and it takes most of the points, and when many
            // events summed into the ratio cost they split it by how much each contributed. Occurrence
            // goes by severity share. Rounded shares may differ from the total by <=1 pt - cosmetic;
            // the actual deduction uses the total below.
            var claimTotal = wanOutages.Sum(Claim);
            foreach (var o in wanOutages)
            {
                var durShare = claimTotal > 0 ? durationPenalty * (Claim(o) / claimTotal) : 0.0;
                var occShare = severityTotal > 0 ? occurrencePenalty * (Severity(o) / severityTotal) : 0.0;
                o.ScorePenaltyPoints = (int)Math.Round(durShare + occShare);
                // Per-event "show your work": time, kind, duration, depth (peak loss), breadth, and the
                // time-of-day usage weight that scaled it, then the duration/occurrence point split.
                _logger?.LogDebug(
                    "ISP Health: outage {Start:HH:mm:ss} {Kind} {Dur}s peakLoss={Peak}% breadth={Deg}/{Tot} usageWeight={UW} -> {Pts} pts ({DurShare} dur + {OccShare} occ)",
                    o.Start, o.IsPartial ? "partial" : o.IsBrief ? "brief" : "full",
                    o.Duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
                    o.PeakLossPct.ToString("0", CultureInfo.InvariantCulture),
                    o.DegradedTargetCount, o.PathTargetCount,
                    o.UsageWeight.ToString("0.00", CultureInfo.InvariantCulture),
                    o.ScorePenaltyPoints,
                    durShare.ToString("0.0", CultureInfo.InvariantCulture),
                    occShare.ToString("0.0", CultureInfo.InvariantCulture));
            }
            _logger?.LogDebug(
                "ISP Health: outage penalty {Penalty} pts = {Dur} duration ({Unavail} unavailability vs {Felt} worst-event floor, {Pct}% of window) + {Occ} occurrence ({Rate}/day over {N} event(s), {Min} eff min) ({Before} -> {After})",
                penalty.ToString("0.#", CultureInfo.InvariantCulture), durationPenalty.ToString("0.#", CultureInfo.InvariantCulture),
                unavailabilityPenalty.ToString("0.#", CultureInfo.InvariantCulture), feltFloor.ToString("0.#", CultureInfo.InvariantCulture),
                downPercent.ToString("0.###", CultureInfo.InvariantCulture),
                occurrencePenalty.ToString("0.#", CultureInfo.InvariantCulture), eventsPerDay.ToString("0.##", CultureInfo.InvariantCulture),
                wanOutages.Count, outageMinutes.ToString("0.#", CultureInfo.InvariantCulture),
                overall, (int)Math.Max(0, Math.Round(overall - penalty)));
            overall = (int)Math.Max(0, Math.Round(overall - penalty));
        }

        // Displayed uptime counts a blackout for its full in-window duration and a partial-loss
        // disruption for the share of it that was actually lost - the detector only declares one past
        // a broad, multi-ASN half-loss, which is an interruption rather than a blip, but 50% loss is
        // not 50% down either. Deliberately NOT usage-weighted, unlike the penalty: how much an outage
        // mattered is a scoring judgment, while uptime is a fact about the line. Local and
        // acknowledged events are excluded on the same grounds as the penalty.
        double DowntimeMinutes(OutageEvent o) => InWindowMinutes(o) *
            (o.IsPartial ? Math.Clamp(o.PeakLossPct / 100.0, 0, 1) : 1.0);
        var downtime = TimeSpan.FromMinutes(wanOutages.Sum(DowntimeMinutes));
        var uptimePercent = windowMinutes > 0
            ? Math.Clamp(100.0 - 100.0 * downtime.TotalMinutes / windowMinutes, 0, 100)
            : 100.0;

        var report = new IspHealthReport
        {
            OverallScore = overall,
            ComputedAt = DateTime.UtcNow,
            WindowStart = inputs.WindowStart,
            WindowEnd = inputs.WindowEnd,
            UptimePercent = uptimePercent,
            Downtime = downtime,
            LossPoolExcludedTargetIds = inputs.LossPoolExcludedTargetIds,
            Profile = profile,
            AccessDimension = accessDimension,
            TransitDimension = transitDimension,
            IspAsnDimension = ispAsnDimension,
            TransitAsns = transitAsns,
            IspAsns = ispAsns,
            IspTargets = inputs.IspTargetSeries.Select(s => BuildIspTargetHealth(s, inputs.FirstHopTargetId, ispHopGrades, _options.RttWinsorPercentile, inputs.NotTracedTargetIds, inputs.TargetAddresses)).ToList(),
            CongestionEvents = inputs.CongestionEvents,
            PathShifts = inputs.PathShifts,
            Outages = inputs.Outages,
            HasExpectedSpeeds = hasExpectedSpeeds,
            HasUpstreamTraceMap = inputs.HopOrderKnown,
            HasLoadedSamples = hasLoadedLatency || hasLoadedLoss,
            ExpectedDownloadMbps = inputs.ExpectedDownloadMbps,
            ExpectedUploadMbps = inputs.ExpectedUploadMbps,
            ExpectedSpeedSource = inputs.ExpectedSpeedSource,
            MeasuredDownloadMbps = bestSpeedTest?.DownloadMbps,
            MeasuredUploadMbps = bestSpeedTest?.UploadMbps,
            TypicalDownloadMbps = typicalDownMbps,
            TypicalUploadMbps = typicalUpMbps,
            SpeedTestTime = bestSpeedTest?.Time
        };
        report.Issues.AddRange(CollectIssues(inputs, profile, report, loadWindows, loadedDeltas));
        report.Issues.AddRange(physicalIssues);
        return report;
    }

    /// <summary>Where the loaded latency evidence came from.</summary>
    internal record LoadedDeltas(double? DownMs, double? UpMs, bool DownFromSpeedTest, bool UpFromSpeedTest)
    {
        public bool FromSpeedTests => DownFromSpeedTest || UpFromSpeedTest;
    }

    /// <summary>
    /// Loaded latency deltas per direction. Passive evidence first: latency samples
    /// inside windows where WAN throughput was solid for LoadWindowSeconds. When a
    /// direction lacks enough passive samples, falls back to the WAN speed tests'
    /// own measurements: loaded latency during the saturating test minus the test's
    /// unloaded ping on the same path.
    /// </summary>
    internal LoadedDeltas ResolveLoadedDeltas(
        IspHealthInputs inputs,
        Dictionary<DateTime, LoadWindow> loadWindows)
    {
        double? down = null, up = null;
        if (loadWindows.Count > 0)
        {
            down = LoadedLatencyDelta(inputs, loadWindows, w => w.IsLoadedDown, w => w.IsLoadedUp);
            up = LoadedLatencyDelta(inputs, loadWindows, w => w.IsLoadedUp, w => w.IsLoadedDown);
        }

        bool downFromSpeedTest = false, upFromSpeedTest = false;
        if (down == null || up == null)
        {
            var (tests, _) = SelectSpeedTests(inputs);
            var downDeltas = tests
                .Where(t => t.DownloadLatencyMs.HasValue && t.PingMs.HasValue)
                .Select(t => Math.Max(0, t.DownloadLatencyMs!.Value - t.PingMs!.Value))
                .ToList();
            var upDeltas = tests
                .Where(t => t.UploadLatencyMs.HasValue && t.PingMs.HasValue)
                .Select(t => Math.Max(0, t.UploadLatencyMs!.Value - t.PingMs!.Value))
                .ToList();
            if (down == null && downDeltas.Count > 0)
            {
                down = SeriesStats.Median(downDeltas);
                downFromSpeedTest = true;
            }
            if (up == null && upDeltas.Count > 0)
            {
                up = SeriesStats.Median(upDeltas);
                upFromSpeedTest = true;
            }
        }
        return new LoadedDeltas(down, up, downFromSpeedTest, upFromSpeedTest);
    }

    /// <summary>
    /// Median RTT of the first clean ISP hop during idle windows. Without load
    /// classification, falls back to the 10th percentile of all RTTs, which
    /// approximates the uncongested floor.
    /// </summary>
    private double? ComputeIdleBaseline(IReadOnlyList<LatencySample> firstHop, Dictionary<DateTime, LoadWindow> loadWindows)
    {
        var rtts = firstHop.Where(s => s.RttAvgMs.HasValue).ToList();
        if (rtts.Count == 0) return null;

        var idleRtts = rtts
            .Where(s => loadWindows.TryGetValue(FloorToWindow(s.Time), out var w) && w.IsIdle)
            .Select(s => s.RttAvgMs!.Value)
            .ToList();
        if (idleRtts.Count > 0) return SeriesStats.WinsorizedMean(idleRtts, _options.RttWinsorPercentile);

        return SeriesStats.Percentile(rtts.Select(s => s.RttAvgMs!.Value).ToList(), 0.10);
    }

    /// <summary>
    /// Picks the WAN speed tests to grade. Prefers those inside the score window; when the
    /// window holds fewer than <see cref="IspHealthOptions.SpeedTestMinSamples"/>, tops up with
    /// the most recent tests from before the window (reaching back no further than
    /// SpeedTestFallbackDays) so a sparse window still grades on a stable sample. Marked stale
    /// only when the window itself is empty - the newest graded test then predates it.
    /// </summary>
    private (List<SpeedTestSample> Tests, bool Stale) SelectSpeedTests(IspHealthInputs inputs)
    {
        var inWindow = inputs.WanSpeedTests
            .Where(t => t.Time >= inputs.WindowStart && t.Time <= inputs.WindowEnd)
            .OrderByDescending(t => t.Time)
            .ToList();
        if (inWindow.Count >= _options.SpeedTestMinSamples) return (inWindow, false);

        var fallbackStart = inputs.WindowEnd.AddDays(-_options.SpeedTestFallbackDays);
        var borrowed = inputs.WanSpeedTests
            .Where(t => t.Time >= fallbackStart && t.Time < inputs.WindowStart)
            .OrderByDescending(t => t.Time)
            .Take(_options.SpeedTestMinSamples - inWindow.Count)
            .ToList();

        var combined = inWindow.Concat(borrowed).ToList();
        if (combined.Count == 0) return (new List<SpeedTestSample>(), false);
        return (combined, inWindow.Count == 0);
    }

    /// <summary>
    /// Grades demonstrated WAN throughput against the configured plan speeds. Per
    /// direction, the lowest SpeedTestOutlierTrimFraction of results is discarded
    /// (broken test servers, flukes), then the score blends the best remaining result
    /// (demonstrated capacity) with the median (typical delivery) so chronically low
    /// tests count without a single bad test tanking the factor.
    /// </summary>
    private (IspScoreFactor Factor, SpeedTestSample? Best, double? TypicalDown, double? TypicalUp) ScoreSpeedVsPlan(IspHealthInputs inputs)
    {
        if (!inputs.ExpectedDownloadMbps.HasValue && !inputs.ExpectedUploadMbps.HasValue)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "Set your ISP speeds in UniFi Network to grade throughput against your plan."
            }, null, null, null);
        }

        var (tests, stale) = SelectSpeedTests(inputs);
        if (tests.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "No recent WAN speed test. Run one (or enable scheduled WAN tests) to grade throughput against your plan."
            }, null, null, null);
        }

        var down = ScoreDirection(tests.Select(t => t.DownloadMbps), inputs.ExpectedDownloadMbps);
        var up = ScoreDirection(tests.Select(t => t.UploadMbps), inputs.ExpectedUploadMbps);
        var scores = new[] { down?.Score, up?.Score }.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "Expected ISP speeds are configured as zero; cannot grade throughput."
            }, null, null, null);
        }

        var bestDown = down?.BestMbps ?? tests.Max(t => t.DownloadMbps);
        var bestUp = up?.BestMbps ?? tests.Max(t => t.UploadMbps);
        var bestTest = tests.OrderByDescending(t => t.DownloadMbps + t.UploadMbps).First();

        var staleNote = stale ? $" Latest test is older than the {_options.ScoreWindowHours} h window." : "";
        var typicalDown = down?.TypicalMbps ?? bestDown;
        var typicalUp = up?.TypicalMbps ?? bestUp;
        var planText = $"{FormatMbps(inputs.ExpectedDownloadMbps ?? 0)} / {FormatMbps(inputs.ExpectedUploadMbps ?? 0)} Mbps plan";
        var multi = tests.Count > 1;
        var description = multi
            ? $"Fastest of {tests.Count} WAN tests vs your {planText}. Typical {FormatMbps(typicalDown)} / {FormatMbps(typicalUp)} Mbps (down / up).{staleNote}"
            : $"Your latest WAN speed test vs your {planText} (down / up).{staleNote}";
        return (new IspScoreFactor
        {
            Name = "Speed vs Plan",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.SpeedVsPlanWeight,
            ValueText = multi ? $"{FormatMbps(bestDown)} / {FormatMbps(bestUp)} Mbps best" : $"{FormatMbps(bestDown)} / {FormatMbps(bestUp)} Mbps",
            Description = description
        }, new SpeedTestSample(bestTest.Time, bestDown, bestUp), down?.TypicalMbps, up?.TypicalMbps);
    }

    /// <summary>
    /// Outlier-trims one direction's results and blends capacity (best) with typical
    /// delivery (median of the rest). Returns the score plus the best and typical for display.
    /// </summary>
    private (double Score, double BestMbps, double TypicalMbps)? ScoreDirection(IEnumerable<double> resultsMbps, double? expectedMbps)
    {
        if (expectedMbps is not > 0) return null;
        var sorted = resultsMbps.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        var trim = (int)Math.Floor(sorted.Count * _options.SpeedTestOutlierTrimFraction);
        var kept = sorted.Skip(Math.Min(trim, sorted.Count - 1)).ToList();

        var best = kept[^1];
        var typical = SeriesStats.Median(kept)!.Value;
        var totalWeight = _options.SpeedCapacityWeight + _options.SpeedTypicalWeight;
        var score = (ScoreSpeedRatio(best / expectedMbps.Value) * _options.SpeedCapacityWeight
                     + ScoreSpeedRatio(typical / expectedMbps.Value) * _options.SpeedTypicalWeight) / totalWeight;
        return (score, best, typical);
    }

    private static double ScoreSpeedRatio(double ratio) => ScoreCurve.Interpolate(ratio,
        (0.2, 0), (0.4, 10), (0.6, 40), (0.8, 70), (0.9, 90), (0.95, 100));

    private IspScoreFactor ScoreIdleLatency(double? idleBaseline, AccessProfile profile)
    {
        if (idleBaseline == null)
        {
            return new IspScoreFactor
            {
                Name = "Idle Latency",
                Weight = _options.IdleLatencyWeight,
                Description = "No ISP hop latency data in the window."
            };
        }

        var mid = (profile.IdleRttNormalLowMs + profile.IdleRttNormalHighMs) / 2.0;
        var score = ScoreCurve.Interpolate(idleBaseline.Value,
            (profile.IdleRttIdealMs, 100),
            (profile.IdleRttNormalLowMs, 96),
            (mid, 92),
            (profile.IdleRttNormalHighMs, 85),
            (profile.IdleRttPoorMs, 25),
            (profile.IdleRttPoorMs * 2, 0));

        return new IspScoreFactor
        {
            Name = "Idle Latency",
            Score = (int)Math.Round(score),
            Weight = _options.IdleLatencyWeight,
            ValueText = FormatMs(idleBaseline.Value),
            Description = $"Idle latency to the first ISP hop vs the {FormatMsBand(profile.IdleRttNormalLowMs)} to {FormatMsBand(profile.IdleRttNormalHighMs)} normal band for {profile.DisplayName}."
        };
    }

    private IspScoreFactor ScorePacketLoss(List<List<LatencySample>> lossPool, AccessProfile profile, double avgLoad)
    {
        // Steady loss is graded on samples OUTSIDE any outage span, so the number reflects
        // true physical-layer loss rather than a discrete internet-down event. Outages are
        // scored separately at the top level (see the outage severity penalty in Score).
        // Loaded samples are INCLUDED here, not filtered out (unlike Idle Latency, which
        // selects a genuinely idle baseline) - the load-calibrated ceiling below compensates
        // for load-driven loss instead. Loaded Loss then re-grades just the loaded subset
        // against the profile's loaded band.
        var losses = lossPool.SelectMany(series => series)
            .Where(s => s.LossPercent.HasValue && !InOutage(s.Time))
            .Select(s => s.LossPercent!.Value)
            .ToList();
        if (losses.Count == 0)
        {
            return new IspScoreFactor
            {
                Name = "Packet Loss",
                Weight = _options.PacketLossWeight,
                Description = "No loss data in the window."
            };
        }

        var meanLoss = losses.Average();
        // Calibrate the acceptable loss ceiling to the average load over the window. An idle
        // line should drop ~nothing; loss only climbs as utilization approaches saturation,
        // so the ceiling rises QUADRATICALLY in load - staying near the idle threshold at low
        // load and reaching the connection's loaded-loss band at LossSaturationLoadFraction
        // (shared-medium access tops out its loss ~75% load, not 100%), holding there above it.
        var t = Math.Clamp(Math.Clamp(avgLoad, 0, 1) / _options.LossSaturationLoadFraction, 0, 1);
        var acceptable = profile.IdleLossAcceptablePct
            + t * t * (profile.LoadedLossDownLowPct - profile.IdleLossAcceptablePct);
        var score = meanLoss <= acceptable
            ? ScoreCurve.Interpolate(meanLoss, (0, 100), (profile.IdleLossIdealPct, 95), (acceptable, 70))
            : ScoreCurve.ExponentialFalloff(meanLoss, acceptable, 70);

        _logger?.LogDebug("ISP Health: packet loss {Loss}% vs load-calibrated ceiling {Ceiling}% ({Load} avg load)",
            meanLoss.ToString("0.###", CultureInfo.InvariantCulture), acceptable.ToString("0.###", CultureInfo.InvariantCulture),
            avgLoad.ToString("0%", CultureInfo.InvariantCulture));

        return new IspScoreFactor
        {
            Name = "Packet Loss",
            Score = (int)Math.Round(score),
            Weight = _options.PacketLossWeight,
            ValueText = FormatPct(meanLoss),
            Description = $"Average loss across ISP, transit, and anycast DNS targets vs the {FormatPct(acceptable)} ceiling for {profile.DisplayName} at {avgLoad.ToString("0%", CultureInfo.InvariantCulture)} average load."
        };
    }

    /// <summary>
    /// Average WAN utilization over the window. Uses the same windowing and per-window
    /// utilization basis as <see cref="LoadClassifier"/> (which drives Loaded Loss): group
    /// rates into LoadWindowSeconds windows, take the busier direction's peak rate in each
    /// as a fraction of the configured plan. Averaged into "average load" here rather than
    /// thresholded into loaded/idle. 0 when there are no expected speeds or no rate data.
    /// </summary>
    private double ComputeAverageLoad(IspHealthInputs inputs)
    {
        var expectedDownBps = inputs.ExpectedDownloadMbps * 1_000_000;
        var expectedUpBps = inputs.ExpectedUploadMbps * 1_000_000;
        if (inputs.WanRates.Count == 0 || (expectedDownBps is null && expectedUpBps is null)) return 0;

        var windowSize = TimeSpan.FromSeconds(_options.LoadWindowSeconds);
        var utils = new List<double>();
        foreach (var group in inputs.WanRates.GroupBy(r => CongestionDetector.FloorTime(r.Time, windowSize)))
        {
            var down = group.Max(r => r.DownloadBps ?? 0);
            var up = group.Max(r => r.UploadBps ?? 0);
            var d = expectedDownBps > 0 ? down / expectedDownBps.Value : 0;
            var u = expectedUpBps > 0 ? up / expectedUpBps.Value : 0;
            utils.Add(Math.Clamp(Math.Max(d, u), 0, 1));
        }
        return utils.Count > 0 ? utils.Average() : 0;
    }

    private (IspScoreFactor Factor, bool HasData) ScoreLoadedLatency(LoadedDeltas deltas, AccessProfile profile)
    {
        var scores = new List<double>();
        if (deltas.DownMs.HasValue) scores.Add(ScoreLoadedDelta(deltas.DownMs.Value, profile));
        if (deltas.UpMs.HasValue) scores.Add(ScoreLoadedDelta(deltas.UpMs.Value, profile));
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Latency",
                Weight = _options.LoadedLatencyWeight,
                Description = "No load on the line and no recent WAN speed test with loaded latency measurements."
            }, false);
        }

        // A negative delta means latency did not rise under load (noise/faster); show
        // it as +0 ms rather than a confusing "+-0.1". Always show both directions; a
        // direction with no loaded samples reads "n/a" (distinct from a measured +0 ms).
        var parts = new List<string>
        {
            deltas.DownMs.HasValue ? $"+{FormatLoadedDelta(deltas.DownMs.Value)} down" : "n/a down",
            deltas.UpMs.HasValue ? $"+{FormatLoadedDelta(deltas.UpMs.Value)} up" : "n/a up"
        };
        var valuedDirections = (deltas.DownMs.HasValue ? 1 : 0) + (deltas.UpMs.HasValue ? 1 : 0);
        var speedTestDirections = (deltas.DownMs.HasValue && deltas.DownFromSpeedTest ? 1 : 0)
            + (deltas.UpMs.HasValue && deltas.UpFromSpeedTest ? 1 : 0);
        var source = speedTestDirections == 0 ? ""
            : speedTestDirections == valuedDirections ? " Measured by WAN speed tests."
            : " Partially determined by WAN speed tests.";

        return (new IspScoreFactor
        {
            Name = "Loaded Latency",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.LoadedLatencyWeight,
            ValueText = string.Join(", ", parts),
            Description = $"Latency increase under load vs +{FormatMsBand(profile.LoadedDeltaExcellentMs)} excellent and +{FormatMsBand(profile.LoadedDeltaAcceptableMs)} acceptable for {profile.DisplayName}.{source}"
        }, true);
    }

    private double ScoreLoadedDelta(double delta, AccessProfile profile)
    {
        var acc = profile.LoadedDeltaAcceptableMs;
        return ScoreCurve.Interpolate(delta,
            (profile.LoadedDeltaExcellentMs, 100),
            (acc, 70),
            (acc * 2, 30),
            (acc * 4, 0));
    }

    /// <summary>
    /// Loaded-latency delta from ISP access hops only. Each access hop's loaded RTT
    /// samples are baseline-subtracted and pooled; the median of the pool (filtered
    /// > 0.5 ms) is the result. Pooling raw samples instead of per-target aggregates
    /// is stable even with sparse loaded data (typical residential). Loaded windows are
    /// dilated (see <see cref="DilateLoadedWindows"/>) so the ramp-in rise and drain tail of
    /// an event - which fall in transition windows outside the strict rate threshold - are
    /// captured rather than dropped.
    /// </summary>
    private double? LoadedLatencyDelta(
        IspHealthInputs inputs,
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector,
        Func<LoadWindow, bool> oppositeSelector)
    {
        const double noiseFloor = 0.5;
        var loaded = DilateLoadedWindows(loadWindows, directionSelector, oppositeSelector);

        var accessCohort = inputs.AccessHopSeries.Count > 0
            ? inputs.AccessHopSeries
            : new List<List<LatencySample>> { inputs.FirstHopSeries };

        var pooledDeltas = new List<double>();
        foreach (var hop in accessCohort)
        {
            var baseline = ComputeIdleBaseline(hop, loadWindows);
            if (baseline == null) continue;

            var deltas = hop
                .Where(s => s.RttAvgMs.HasValue && loaded.Contains(FloorToWindow(s.Time)))
                .Select(s => s.RttAvgMs!.Value - baseline.Value);

            pooledDeltas.AddRange(deltas);
        }

        var credible = pooledDeltas.Where(d => d >= noiseFloor).ToList();
        if (credible.Count < _options.MinLoadedSamples) return null;
        return Math.Max(0, SeriesStats.Median(credible)!.Value);
    }

    private (IspScoreFactor Factor, bool HasData) ScoreLoadedLoss(
        List<List<LatencySample>> lossPool,
        Dictionary<DateTime, LoadWindow> loadWindows,
        AccessProfile profile)
    {
        if (loadWindows.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Loss",
                Weight = _options.LoadedLossWeight,
                Description = "Loaded loss needs expected ISP speeds and load on the line."
            }, false);
        }

        var downLoss = LoadedMeanLoss(lossPool, loadWindows, w => w.IsLoadedDown, w => w.IsLoadedUp);
        var upLoss = LoadedMeanLoss(lossPool, loadWindows, w => w.IsLoadedUp, w => w.IsLoadedDown);

        var scores = new List<double>();
        if (downLoss.HasValue) scores.Add(ScoreLossBand(downLoss.Value, profile.LoadedLossDownLowPct, profile.LoadedLossDownHighPct));
        if (upLoss.HasValue) scores.Add(ScoreLossBand(upLoss.Value, profile.LoadedLossUpLowPct, profile.LoadedLossUpHighPct));
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Loss",
                Weight = _options.LoadedLossWeight,
                Description = "The line was never under sustained load during the window."
            }, false);
        }

        // Always show both directions; a direction with no loaded samples reads "n/a"
        // (distinct from a measured 0%).
        var parts = new List<string>
        {
            downLoss.HasValue ? $"{FormatPct(downLoss.Value)} down" : "n/a down",
            upLoss.HasValue ? $"{FormatPct(upLoss.Value)} up" : "n/a up"
        };

        return (new IspScoreFactor
        {
            Name = "Loaded Loss",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.LoadedLossWeight,
            ValueText = string.Join(", ", parts),
            Description = $"Packet loss while the line is under load vs the {FormatPct(profile.LoadedLossDownLowPct)} to {FormatPct(profile.LoadedLossDownHighPct)} downstream band for {profile.DisplayName}."
        }, true);
    }

    /// <summary>
    /// Loaded loss degrades on a linear tail rather than the idle-loss exponential:
    /// some loss under full load is expected behavior, so 1.67x the band ceiling
    /// should read "needs work" (~57), not collapse to single digits.
    /// </summary>
    private double ScoreLossBand(double loss, double bandLow, double bandHigh)
    {
        return ScoreCurve.Interpolate(loss,
            (0, 100), (bandLow, 90), (bandHigh, 70),
            (bandHigh * 2, 50), (bandHigh * 3, 32), (bandHigh * 5, 12), (bandHigh * 8, 0));
    }

    private double? LoadedMeanLoss(
        List<List<LatencySample>> lossPool,
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector,
        Func<LoadWindow, bool> oppositeSelector)
    {
        var loaded = DilateLoadedWindows(loadWindows, directionSelector, oppositeSelector);
        var losses = lossPool.SelectMany(series => series)
            .Where(s => s.LossPercent.HasValue && !InOutage(s.Time)
                && loaded.Contains(FloorToWindow(s.Time)))
            .Select(s => s.LossPercent!.Value)
            .ToList();
        // Loaded loss rests on however many samples happen to fall inside the loaded windows, and on
        // a long window the rate series is aggregated far coarser than LoadWindowSeconds, so that set
        // can be small enough for a few dark samples to set the whole figure. Log what it was built
        // from - a mean over a handful of samples is a very different claim from one over thousands.
        _logger?.LogDebug(
            "ISP Health: loaded loss pool {Count} sample(s) from {Windows} loaded window key(s), {Dark} at/above 99% ({Mean}% mean)",
            losses.Count, loaded.Count, losses.Count(l => l >= 99.0),
            losses.Count > 0 ? losses.Average().ToString("0.##", CultureInfo.InvariantCulture) : "n/a");
        if (losses.Count < _options.MinLoadedSamples) return null;
        return losses.Average();
    }

    /// <summary>
    /// Window keys that count as loaded in a direction for sample matching: the directly
    /// loaded windows plus up to <see cref="IspHealthOptions.LoadedLeadSeconds"/> before and
    /// <see cref="IspHealthOptions.LoadedTailSeconds"/> after each loaded run. The ramp fills
    /// the queue before throughput crosses the loaded threshold and the drain (plus end-stamped
    /// loss probes) trails it, so without dilation the edges of every event are dropped. Dilation
    /// never crosses into a window loaded in the OPPOSITE direction, so a speed test's download
    /// tail does not bleed into its upload phase. Idle classification is unaffected (this builds a
    /// loaded set only), keeping the baseline a clean uncongested floor.
    /// </summary>
    // Loaded window keys per direction for the current report. Both lists are filled on the first
    // request from a single pass, since the dilation asks for them four times.
    private List<DateTime>? _loadedDownKeys;
    private List<DateTime>? _loadedUpKeys;

    /// <summary>The keys the selector calls loaded, scanning the window dictionary at most once.</summary>
    private List<DateTime> LoadedKeysFor(Dictionary<DateTime, LoadWindow> loadWindows, Func<LoadWindow, bool> directionSelector)
    {
        if (_loadedDownKeys == null || _loadedUpKeys == null)
        {
            var down = new List<DateTime>();
            var up = new List<DateTime>();
            foreach (var (key, w) in loadWindows)
            {
                if (w.IsLoadedDown) down.Add(key);
                if (w.IsLoadedUp) up.Add(key);
            }
            _loadedDownKeys = down;
            _loadedUpKeys = up;
        }
        // The selector is one of the two direction predicates; probe it with a window loaded in
        // download only, which the up selector rejects.
        return directionSelector(new LoadWindow(false, true, false)) ? _loadedDownKeys : _loadedUpKeys;
    }

    private HashSet<DateTime> DilateLoadedWindows(
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector,
        Func<LoadWindow, bool> oppositeSelector)
    {
        var leadWindows = (int)Math.Ceiling((double)_options.LoadedLeadSeconds / _options.LoadWindowSeconds);
        var tailWindows = (int)Math.Ceiling((double)_options.LoadedTailSeconds / _options.LoadWindowSeconds);

        var loaded = new HashSet<DateTime>();
        // Only the loaded keys matter, and there are a couple of dozen of them against six figures of
        // windows on a long span. This runs four times - latency and loss, each direction - so the
        // scan is cached per Score() call rather than repeated.
        foreach (var key in LoadedKeysFor(loadWindows, directionSelector))
        {
            loaded.Add(key);
            for (var i = 1; i <= leadWindows; i++)
            {
                var k = key.AddSeconds(-i * _options.LoadWindowSeconds);
                if (loadWindows.TryGetValue(k, out var nw) && oppositeSelector(nw)) break;
                loaded.Add(k);
            }
            for (var i = 1; i <= tailWindows; i++)
            {
                var k = key.AddSeconds(i * _options.LoadWindowSeconds);
                if (loadWindows.TryGetValue(k, out var nw) && oppositeSelector(nw)) break;
                loaded.Add(k);
            }
        }
        return loaded;
    }

    /// <summary>
    /// Grades one ASN (transit) or one ISP hop: a quality blend (stability, jitter,
    /// loss, congestion) capped by a reach ceiling. Jitter and stability come from
    /// <see cref="AsnSeries.JitterSourceSamples"/> when set (a transit ASN's farther
    /// cluster, to discount false near-hop jitter), otherwise the series itself.
    /// Two reach modes: <paramref name="accessBaselineRtt"/> + <paramref name="internetMedianDeltaMs"/>
    /// is the transit ceiling (distance normalized against the measured internet
    /// context); <paramref name="intraAsnFloorRttMs"/> is the ISP intra-ASN ceiling (a
    /// soft penalty for hops sitting further out than this ISP's nearest hop). Quality
    /// deficits subtract below the ceiling, so congestion and jitter always count.
    /// </summary>
    private IspAsnHealth GradeAsn(
        AsnSeries series,
        List<CongestionEvent> congestionEvents,
        double? jitterFloorMs,
        double? accessBaselineRtt,
        double? internetMedianDeltaMs,
        double? intraAsnFloorRttMs = null,
        double? jitterOverrideMs = null,
        double? stabilityMadOverrideMs = null)
    {
        var rtts = series.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var losses = series.Samples.Where(s => s.LossPercent.HasValue && !InOutage(s.Time)).Select(s => s.LossPercent!.Value).ToList();
        var jitters = series.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();

        // Sort the RTT set once; median, MAD, and the winsorized-mean / P95 below all read from it.
        var sortedRtts = rtts.ToArray();
        Array.Sort(sortedRtts);
        var medianRtt = SeriesStats.MedianSorted(sortedRtts);
        var mad = medianRtt.HasValue ? SeriesStats.MadSorted(sortedRtts, medianRtt.Value) : null;

        // Jitter and stability are absolve-only across clusters. The nearest cluster's
        // variance can be false (ICMP deprioritization at that hop); a cleaner farther
        // cluster - reached through it - proves the path is steady, so we take the BETTER
        // (lower) of near and far. We never take the worse: a jittery farther cluster is
        // its own problem further along the path and must not downgrade the nearer cluster.
        // An ISP hop instead takes the ISP-wide jitter bound (jitterOverrideMs), which is
        // already capped by the cleanest transit ASN.
        var nearJitter = ScoringJitterOf(series.Samples);
        var rawEffectiveJitter = jitterOverrideMs ?? EffectiveLower(series.Samples, series.JitterSourceSamples, ScoringJitterOf);
        // Don't assimilate on a trivial difference: a witness must sit at least the minimum
        // delta below this series' own reading to pull it down. Within that band it's noise,
        // so keep our own jitter (no absolve, no assimilation flag). Applies to ISP and transit.
        var effectiveJitter = rawEffectiveJitter.HasValue && nearJitter.HasValue
            && rawEffectiveJitter.Value > nearJitter.Value - _options.JitterAssimilationMinDeltaMs
            ? nearJitter
            : rawEffectiveJitter;
        // RTT stability: take the steadier (lower absolute MAD) of this ASN's near and far clusters.
        // Working in absolute MAD (not the ratio) keeps the near/far min honest and lets cross-target
        // witness absolution (stabilityMadOverrideMs) compare MAD across targets sitting at different
        // base RTTs. A witness proven to route through this hop that carries steadier end-to-end RTT
        // proves the hop's own wander is a per-hop artifact (ICMP-deprioritized control plane), so it
        // pulls the MAD down - absolve-only, and only past the assimilation band so a trivially-lower
        // witness doesn't snap it. The resulting absolute MAD is graded against the per-tech band below.
        var withinMad = EffectiveLower(series.Samples, series.JitterSourceSamples, RttMadOf);
        var stabilityMad = withinMad;
        if (stabilityMadOverrideMs.HasValue
            && (!withinMad.HasValue || stabilityMadOverrideMs.Value < withinMad.Value - _options.StabilityAssimilationMinMadMs))
            stabilityMad = stabilityMadOverrideMs.Value;

        // Assimilated when a witness (a farther transit cluster, or - for an ISP hop via
        // the override - a downstream transit/deeper ISP hop) pulled this jitter below the
        // series' own nearest reading.
        var jitterAssimilated = effectiveJitter.HasValue
            && nearJitter.HasValue && effectiveJitter.Value < nearJitter.Value - 0.001;

        if (jitterOverrideMs == null && series.JitterSourceSamples.Count > 0)
        {
            _logger?.LogDebug(
                "ISP Health: AS{Asn} ({Name}) jitter absolve - near {Near} ms, farther cluster {Far} ms, effective {Eff} ms",
                series.AsnNumber, series.AsnName, FormatMsOrNull(ScoringJitterOf(series.Samples)),
                FormatMsOrNull(ScoringJitterOf(series.JitterSourceSamples)), FormatMsOrNull(effectiveJitter));
        }

        int? stabilityScore = stabilityMad.HasValue
            ? (int)Math.Round(ScoreStabilityMad(stabilityMad.Value, medianRtt))
            : null;

        int? jitterScore = effectiveJitter.HasValue
            ? (int)Math.Round(ScoreJitterVsFloor(effectiveJitter.Value, jitterFloorMs))
            : null;

        // Reach ceiling: the best grade this hop's distance allows.
        double? reachDelta = null;
        int? reachCeiling = null;
        if (intraAsnFloorRttMs.HasValue && medianRtt.HasValue)
        {
            // ISP intra-ASN reach: distance from this ISP's nearest hop. A second POP a
            // couple ms out is two sites a real distance apart - nominal, not a fault -
            // so it tops out short of perfect rather than getting dinged hard.
            reachDelta = Math.Max(0, medianRtt.Value - intraAsnFloorRttMs.Value);
            var cIntra = ScoreCurve.Interpolate(reachDelta.Value,
                (0, 100), (1, 93), (2, 85), (4, 70), (8, 50), (16, 35));

            // Item D: lift-only blend toward the internet-relative ceiling. Keeps the intra-ASN
            // distance truth as the floor, but absolves a hop that's modest relative to where the
            // internet actually sits, so a geographically large access network isn't punished for
            // normal in-region distance. Never lowers; partial so genuine distance always shows.
            var ceiling = cIntra;
            if (accessBaselineRtt.HasValue && internetMedianDeltaMs is > 0)
            {
                var netDelta = Math.Max(0, medianRtt.Value - accessBaselineRtt.Value);
                var ratio = netDelta / Math.Max(internetMedianDeltaMs.Value, 2.0);
                var cNet = ScoreCurve.Interpolate(ratio,
                    (0.5, 100), (1.0, 93), (1.5, 90), (2.0, 85), (3.0, 65), (5.0, 40));
                ceiling = cIntra + _options.AccessReachInternetBlendAlpha * Math.Max(0, cNet - cIntra);
            }
            reachCeiling = (int)Math.Round(ceiling);
        }
        else if (accessBaselineRtt.HasValue && medianRtt.HasValue)
        {
            // Transit reach ceiling. The absolute curve applies only top-end gravity
            // (100 needs sub +1 ms; +7-9 ms tops out ~93; far distance alone never grades
            // below the high 80s). The relative curve judges distance against the measured
            // internet context: ratio of this POP's delta to the median internet-target
            // delta. Validated against rural data where a clean 22 ms POP (1.6x internet
            // distance) must stay solid.
            reachDelta = Math.Max(0, medianRtt.Value - accessBaselineRtt.Value);
            var ceiling = ScoreCurve.Interpolate(reachDelta.Value,
                (1, 100), (8, 93), (15, 90), (30, 87), (60, 82));
            if (internetMedianDeltaMs is > 0)
            {
                var ratio = reachDelta.Value / Math.Max(internetMedianDeltaMs.Value, 2.0);
                var relative = ScoreCurve.Interpolate(ratio,
                    (0.5, 100), (1.0, 93), (1.5, 90), (2.0, 85), (3.0, 65), (5.0, 40));
                ceiling = Math.Min(ceiling, relative);
            }
            reachCeiling = (int)Math.Round(ceiling);
        }

        int? lossScore = null;
        if (losses.Count > 0)
        {
            // Forgiving anchors: transit routers often deprioritize ICMP under
            // control-plane policing, so hop loss overstates real forwarding loss
            lossScore = (int)Math.Round(ScoreCurve.Interpolate(losses.Average(),
                (0, 100), (0.1, 95), (0.5, 80), (1, 65), (2, 45), (5, 20), (10, 0)));
        }

        // Attribute congestion to this card by role, not bare ASN: the same ASN can be
        // both the access ISP and a transit provider, so an event is only counted when
        // it fired on one of this role's targets. Events without target info (e.g. unit
        // tests) fall back to ASN matching.
        var roleTargets = series.RoleTargetIds.Count > 0 ? series.RoleTargetIds : series.TargetIds;
        var roleTargetSet = new HashSet<string>(roleTargets);
        // Only confirmed congestion penalizes a network. Self-inflicted bufferbloat,
        // absolved control-plane (ICMP) noise, and unverifiable dead-end elevations are
        // surfaced in the report but never ding the ASN's grade.
        var asnEvents = congestionEvents
            .Where(e => e.Disposition == CongestionDisposition.Confirmed
                && e.AsnNumbers.Contains(series.AsnNumber)
                && (e.TargetIds.Count == 0 || e.TargetIds.Any(t => roleTargetSet.Contains(t))))
            .ToList();
        // Union of the event windows, not the sum - two hops of the same ASN degrading in the
        // same window (e.g. parallel backbone links, or a dead-end hop confirmed by its sibling)
        // are one incident and must not double-count the congestion hours.
        var eventHours = UnionHours(asnEvents);
        var congestionScore = (int)Math.Round(Math.Max(0, 100 - _options.CongestionPenaltyPerHour * eventHours));

        int? overall = null;
        var weighted = new List<(double Score, double Weight)>();
        if (stabilityScore.HasValue) weighted.Add((stabilityScore.Value, _options.AsnLatencyStabilityWeight));
        if (jitterScore.HasValue) weighted.Add((jitterScore.Value, _options.AsnJitterWeight));
        if (lossScore.HasValue) weighted.Add((lossScore.Value, _options.AsnLossWeight));
        weighted.Add((congestionScore, _options.AsnCongestionWeight));
        if (stabilityScore.HasValue || jitterScore.HasValue)
        {
            var totalWeight = weighted.Sum(w => w.Weight);
            var quality = weighted.Sum(w => w.Score * w.Weight) / totalWeight;
            // Quality deficits subtract below the ceiling so congestion, loss, and
            // jitter always move the grade even on distant POPs
            overall = (int)Math.Round(Math.Max(0, (reachCeiling ?? 100) - (100 - quality)));
        }

        return new IspAsnHealth
        {
            AsnNumber = series.AsnNumber,
            AsnName = series.AsnName,
            TargetIds = series.TargetIds,
            MedianRttMs = medianRtt,
            // Displayed RTT: winsorized mean (P99-capped) so sustained elevation shows but a
            // flap can't distort it. Reach (above) stays on the median - that measures distance.
            MeanRttMs = SeriesStats.WinsorizedMeanSorted(sortedRtts, _options.RttWinsorPercentile),
            P95RttMs = SeriesStats.PercentileSorted(sortedRtts, 0.95),
            // Raw near-cluster median, informational only. The displayed and scored jitter
            // is the effective (absolve/assimilated) value below.
            MedianJitterMs = jitters.Count > 0 ? SeriesStats.Median(jitters) : null,
            // The effective jitter: absolve-only across clusters (transit) or the ISP-wide
            // bound (ISP). This is what the card shows and what the ISP cap reads, so the
            // displayed value reflects the assimilation rather than the raw near hop.
            ScoredJitterMs = effectiveJitter,
            RttMadMs = mad,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            ReachDeltaMs = reachDelta,
            LatencyStabilityScore = stabilityScore,
            JitterScore = jitterScore,
            LossScore = lossScore,
            ReachLatencyScore = reachCeiling,
            CongestionScore = congestionScore,
            OverallScore = overall,
            CongestionEventCount = asnEvents.Count,
            JitterAssimilated = jitterAssimilated,
            RawJitterMs = nearJitter
        };
    }

    /// <summary>The path jitter floor: the lowest scoring (P90) jitter across all ISP hops
    /// and transit clusters. Null when no series carries jitter.</summary>
    private double? ComputeJitterFloor(IspHealthInputs inputs)
    {
        var medians = new List<double>();
        void Add(IReadOnlyList<LatencySample> samples)
        {
            var m = ScoringJitterOf(samples);
            if (m.HasValue) medians.Add(m.Value);
        }
        foreach (var s in inputs.IspAsnSeries) Add(s.Samples);
        foreach (var s in inputs.TransitAsnSeries)
        {
            Add(s.Samples);
            if (s.JitterSourceSamples.Count > 0) Add(s.JitterSourceSamples);
        }
        return medians.Count > 0 ? medians.Min() : null;
    }

    /// <summary>
    /// The jitter statistic used for scoring, the ISP/transit cap, and the cards: the
    /// <see cref="IspHealthOptions.AsnJitterScoringPercentile"/> (P90) of the effective jitter.
    /// A tail percentile - not the median - because the tail is what the cards show, what users
    /// reason about, and what hurts real-time traffic; P90 rather than P95 so a link's harshest
    /// few percent of samples don't dominate the quality arm (intermittent bursts are caught by
    /// the separate congestion detector). The ISP and transit jitter shown and scored are the same
    /// value. Null when none reported jitter.
    /// </summary>
    private double? ScoringJitterOf(IReadOnlyList<LatencySample> samples)
    {
        var js = samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        return js.Count > 0 ? SeriesStats.Percentile(js, _options.AsnJitterScoringPercentile) : null;
    }

    /// <summary>Absolute RTT MAD (median absolute deviation, ms) of a sample set; lower is steadier.
    /// Null without RTT. Callers normalize by the hop's own median (scale-free stability ratio) and
    /// apply the <see cref="IspHealthOptions.StabilityMadFloorMs"/> dead-band.</summary>
    private static double? RttMadOf(IReadOnlyList<LatencySample> samples)
    {
        var rtts = samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToArray();
        Array.Sort(rtts);
        var median = SeriesStats.MedianSorted(rtts);
        return median.HasValue ? SeriesStats.MadSorted(rtts, median.Value) : null;
    }

    /// <summary>
    /// The better (lower) of a metric over the near samples and over the far samples -
    /// absolve-only. A cleaner farther cluster pulls the value down (the near hop's jitter
    /// was false); a worse farther cluster is ignored so it never downgrades the nearer
    /// hop. Far empty means near only.
    /// </summary>
    private static double? EffectiveLower(IReadOnlyList<LatencySample> near, IReadOnlyList<LatencySample> far, Func<IReadOnlyList<LatencySample>, double?> metric)
    {
        var n = metric(near);
        if (far.Count == 0) return n;
        var f = metric(far);
        if (!f.HasValue) return n;
        if (!n.HasValue) return f;
        return Math.Min(n.Value, f.Value);
    }

    /// <summary>
    /// Floor-relative jitter score. A target at the floor is as stable as the line
    /// allows (100). Above it the target is genuinely jittery even if it is only ICMP
    /// deprioritization. Dual-slope: a gentle slope through a dead band just above the
    /// floor (+25-50%), then a steeper drop, so 2x the floor reads as a clear signal.
    /// The high end is absolute - 5+ ms is real jitter no matter how low the floor sits.
    /// </summary>
    private double ScoreJitterVsFloor(double jitterMs, double? floorMs)
    {
        // Item E: when the access technology defines a jitter band, grade straight off it so the
        // medium's inherent jitter (e.g. DOCSIS ~3 ms) reads as normal. The floor is the per-tech
        // ideal - not the measured path floor - so a single quiet sample can't drag the 100-anchor
        // below what the medium really does. Applies to ISP and transit alike (every probe crosses
        // the access medium). Techs with no band (neutral / PPPoE / Other) keep the floor curve.
        if (_profile is { JitterIdealMs: { } ideal, JitterTypicalMs: { } typical, JitterPoorMs: { } poor })
        {
            return ScoreCurve.Interpolate(jitterMs,
                (ideal, 100), (typical, 90), (poor, 25), (2.0 * poor, 0));
        }

        var f = Math.Clamp(floorMs ?? 0.4, _options.JitterFloorMinMs, _options.JitterFloorMaxMs);
        return ScoreCurve.Interpolate(jitterMs,
            (f, 100), (1.25 * f, 96), (1.5 * f, 91), (2.0 * f, 70), (5.0, 22), (12.0, 0));
    }

    /// <summary>
    /// Scores RTT stability (absolute MAD, ms) against the per-tech band. The stability analog of
    /// <see cref="ScoreJitterVsFloor"/>: a medium's inherent RTT wander reads as normal (fiber's
    /// sub-ms, Starlink's several-ms) instead of being punished by the scale-free MAD/median ratio,
    /// which over-weights proportional variation on low-RTT media. The MAD here is post-absolution,
    /// so per-hop ICMP-deprioritization artifact has already been stripped. Techs with no band
    /// (Unknown) fall back to the floored ratio, so we never over-punish sub-ms wander on a fast
    /// line we can't classify.
    /// </summary>
    private double ScoreStabilityMad(double madMs, double? medianRttMs)
    {
        if (_profile is { StabilityMadIdealMs: { } ideal, StabilityMadTypicalMs: { } typical, StabilityMadPoorMs: { } poor })
            return ScoreCurve.Interpolate(madMs, (ideal, 100), (typical, 90), (poor, 25), (2.0 * poor, 0));

        if (medianRttMs is > 0)
        {
            var ratio = Math.Max(0, madMs - _options.StabilityMadFloorMs) / medianRttMs.Value;
            return ScoreCurve.Interpolate(ratio, (0.02, 100), (0.10, 80), (0.25, 55), (0.5, 25), (1.0, 0));
        }
        return 100;
    }

    /// <summary>
    /// Grades every ISP hop. Each hop's jitter is absolved per-hop, routes-through-gated: a
    /// witness (a transit ASN, another ISP hop, or a monitored destination) may only pull a
    /// hop's jitter down when the hop is in the witness's ancestor set - proven upstream of it
    /// on a shared discovery trace - so a divergent clean transit can never clear a congested
    /// hop it doesn't traverse. When no ancestor data exists (no re-discovery yet) the gate
    /// falls open for transit (transit is always downstream of the ISP) and stays closed for
    /// ISP siblings and destinations. A destination's clean end-to-end jitter is a hard upper
    /// bound on any on-path hop's true jitter, so a smooth path to it absolves an
    /// ICMP-deprioritized hop whose forwarded traffic actually reaches the destination cleanly.
    /// Hops are also scored against the intra-ASN reach floor (distance, not a fault).
    /// </summary>
    /// <summary>
    /// Grades every transit ASN. Beyond the within-ASN far-cluster absolution baked into each
    /// series' <see cref="AsnSeries.JitterSourceSamples"/>, a transit ASN's jitter is additionally
    /// absolved (Arm A) by any monitored destination - or any OTHER transit ASN - proven to route
    /// through it (routes-through gated on stored ancestry): a clean end-to-end path across the ASN
    /// is an upper bound on the ASN's true jitter, so an ICMP-deprioritized transit router isn't
    /// penalized for control-plane noise its forwarded traffic never sees. Strict: with no ancestry
    /// (hopOrderKnown false) nothing absolves, and a witness only counts where it provably routes
    /// through the ASN - never on faith. Falls back to the base within-ASN grade when no witness
    /// applies.
    /// </summary>
    private List<IspAsnHealth> GradeTransitAsns(
        List<AsnSeries> transitSeries,
        List<AsnSeries> destinationSeries,
        bool hopOrderKnown,
        List<CongestionEvent> congestionEvents,
        double? jitterFloorMs,
        double? accessBaselineRtt,
        double? internetMedianDeltaMs)
    {
        // Base grade (within-ASN far-cluster absolution only) - the prior behavior. Serves as the
        // fallback and as each ASN's own jitter when it witnesses another ASN.
        var baseGrades = transitSeries
            .Select(s => (Series: s, Grade: GradeAsn(s, congestionEvents, jitterFloorMs, accessBaselineRtt, internetMedianDeltaMs)))
            .ToList();

        // Destination end-to-end jitter + each transit ASN's base jitter, keyed by the ancestor IPs
        // that prove what they route through. Destinations only when ancestry exists (strict).
        var destWitnesses = hopOrderKnown
            ? destinationSeries
                .Select(d => (d.AncestorIps, Jitter: ScoringJitterOf(d.Samples)))
                .Where(w => w.Jitter.HasValue)
                .Select(w => (w.AncestorIps, Jitter: w.Jitter!.Value))
                .ToList()
            : new List<(List<string> AncestorIps, double Jitter)>();
        var transitWitnesses = baseGrades
            .Where(b => b.Grade.ScoredJitterMs.HasValue)
            .Select(b => (b.Series.AsnNumber, b.Series.AncestorIps, Jitter: b.Grade.ScoredJitterMs!.Value))
            .ToList();

        // Stability witnesses in absolute RTT MAD, same sources and routes-through gate as jitter:
        // a destination or a different transit ASN proven to route through this ASN whose end-to-end
        // RTT is steadier bounds this ASN's true wander (its own MAD is a per-hop artifact).
        var destMadWitnesses = hopOrderKnown
            ? destinationSeries
                .Select(d => (d.AncestorIps, Mad: RttMadOf(d.Samples)))
                .Where(w => w.Mad.HasValue)
                .Select(w => (w.AncestorIps, Mad: w.Mad!.Value))
                .ToList()
            : new List<(List<string> AncestorIps, double Mad)>();
        var transitMadWitnesses = transitSeries
            .Select(s => (s.AsnNumber, s.AncestorIps, Mad: RttMadOf(s.Samples)))
            .Where(w => w.Mad.HasValue)
            .Select(w => (w.AsnNumber, w.AncestorIps, Mad: w.Mad!.Value))
            .ToList();

        var grades = new List<IspAsnHealth>();
        foreach (var (series, baseGrade) in baseGrades)
        {
            // The jitter the base grade scored on: near vs this ASN's own farther cluster.
            var within = EffectiveLower(series.Samples, series.JitterSourceSamples, ScoringJitterOf);
            var witnesses = destWitnesses
                .Where(w => hopOrderKnown && RoutesThrough(w.AncestorIps, series.HopIps))
                .Select(w => w.Jitter)
                .Concat(transitWitnesses
                    .Where(w => hopOrderKnown && w.AsnNumber != series.AsnNumber && RoutesThrough(w.AncestorIps, series.HopIps))
                    .Select(w => w.Jitter))
                .ToList();
            var stabWitnesses = destMadWitnesses
                .Where(w => hopOrderKnown && RoutesThrough(w.AncestorIps, series.HopIps))
                .Select(w => w.Mad)
                .Concat(transitMadWitnesses
                    .Where(w => hopOrderKnown && w.AsnNumber != series.AsnNumber && RoutesThrough(w.AncestorIps, series.HopIps))
                    .Select(w => w.Mad))
                .ToList();
            double? stabOverride = stabWitnesses.Count > 0 ? stabWitnesses.Min() : (double?)null;
            if (witnesses.Count == 0 && stabOverride == null)
            {
                grades.Add(baseGrade);
                continue;
            }
            double? jitterOverride = witnesses.Count > 0
                ? (within.HasValue ? Math.Min(within.Value, witnesses.Min()) : witnesses.Min())
                : null;
            var transitGrade = GradeAsn(series, congestionEvents, jitterFloorMs, accessBaselineRtt, internetMedianDeltaMs,
                jitterOverrideMs: jitterOverride, stabilityMadOverrideMs: stabOverride);
            _logger?.LogDebug(
                "ISP Health: transit AS{Asn} ({Name}) graded {Score} - jitter within {Within} ms -> effective {Eff} ms ({JWit} witnesses); stability MAD measured {Mad} ms -> witness-floor {StabWit} ms ({SWit} witnesses) score {StabScore}",
                series.AsnNumber, series.AsnName, transitGrade.OverallScore,
                FormatMsOrNull(within), FormatMsOrNull(transitGrade.ScoredJitterMs), witnesses.Count,
                FormatMsOrNull(RttMadOf(series.Samples)), FormatMsOrNull(stabOverride), stabWitnesses.Count, transitGrade.LatencyStabilityScore);
            grades.Add(transitGrade);
        }
        return grades;
    }

    /// <summary>
    /// Selects the monitored internet destinations reached over the access ISP's own peering/IX rather
    /// than a transit provider: the forward path crosses no transit ASN AND the best-case delta beyond
    /// the first clean ISP hop is under <see cref="IxPeeringMaxBestCaseDeltaMs"/>. Requires
    /// per-destination ancestry (hop order + a non-empty ancestor set); a destination we can't place on
    /// the path is never assumed peered. Empty when nothing qualifies.
    /// </summary>
    private List<AsnSeries> SelectPeeringReachedDestinations(IspHealthInputs inputs)
    {
        if (!inputs.HopOrderKnown) return new List<AsnSeries>();

        // Best-case (min) RTT of the first clean ISP hop: the access baseline the delta subtracts, so
        // the threshold measures the incremental peering-path latency, not the access medium's own.
        var firstHopBestCase = inputs.FirstHopSeries
            .Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value)
            .DefaultIfEmpty(double.NaN).Min();
        var baseline = double.IsNaN(firstHopBestCase) ? 0.0 : firstHopBestCase;

        double? BestCaseDeltaMs(AsnSeries d)
        {
            var min = d.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value)
                .DefaultIfEmpty(double.NaN).Min();
            return double.IsNaN(min) ? (double?)null : Math.Max(0, min - baseline);
        }

        // Min (best case), not mean: a peered-but-flapping anycast (a low floor with occasional spikes)
        // is still peering - the flapping belongs in the grade, not the peering/transit classification.
        return inputs.DestinationSeries
            .Where(d => d.AncestorIps.Count > 0
                && !inputs.TransitAsnSeries.Any(t => RoutesThrough(d.AncestorIps, t.HopIps))
                && BestCaseDeltaMs(d) is double delta && delta < IxPeeringMaxBestCaseDeltaMs)
            .ToList();
    }

    /// <summary>
    /// Grades the direct-peered destinations (from <see cref="SelectPeeringReachedDestinations"/>) as one
    /// synthetic "IX Peering" transit entry. Each destination is graded on ITS OWN series on the same
    /// jitter/loss/stability/reach basis as a real transit ASN, and the grades are AVERAGED - the series are
    /// never pooled. Pooling samples across targets at different RTT baselines (a flat ~2 ms peer beside a
    /// flat ~6 ms peer) manufactures cross-target variance that craters the stability sub-score, and lets a
    /// single target's jitter tail dominate the pooled P90; per-peer grading keeps each target coherent and
    /// lets one degraded destination count only 1/N. Distance stays low (peered), so each reach ceiling
    /// barely bites and the grade is driven by jitter and loss. A proximity absolution then buys back a
    /// fraction of the jitter penalty when peering delivers the internet far closer than the transit
    /// alternative (<paramref name="transitReferenceRttMs"/>). Per-member grades are logged at Debug.
    /// </summary>
    private IspAsnHealth GradeIxPeering(
        List<AsnSeries> peeringReached,
        List<CongestionEvent> congestionEvents,
        double? jitterFloorMs,
        double? accessBaselineRtt,
        double? internetMedianDeltaMs,
        double? transitReferenceRttMs)
    {
        var perPeer = peeringReached
            .Select(d => GradeAsn(d, congestionEvents, jitterFloorMs, accessBaselineRtt, internetMedianDeltaMs))
            .ToList();

        foreach (var p in perPeer)
        {
            _logger?.LogDebug(
                "ISP Health: IX Peering member {Name} -> {Overall} (stability {Stab}, jitter {Jit} @ P{Pct} {Pj} ms, loss {Loss} @ {LossPct}%, reach ceiling {Reach})",
                p.AsnName, p.OverallScore, p.LatencyStabilityScore, p.JitterScore, (int)Math.Round(_options.AsnJitterScoringPercentile * 100), FormatMsOrNull(p.ScoredJitterMs),
                p.LossScore, p.LossPct?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a", p.ReachLatencyScore);
        }

        double? AvgOf(Func<IspAsnHealth, double?> sel)
        {
            var vals = perPeer.Select(sel).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? vals.Average() : null;
        }
        int? AvgIntOf(Func<IspAsnHealth, int?> sel)
        {
            var vals = perPeer.Select(sel).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            return vals.Count > 0 ? (int?)Math.Round(vals.Average()) : null;
        }

        var meanRtt = AvgOf(p => p.MeanRttMs);
        var jitterScore = AvgIntOf(p => p.JitterScore);
        var overall = AvgIntOf(p => p.OverallScore);

        // Proximity jitter absolution. A few ms of jitter on a peered path that reaches the internet in a
        // fraction of the transit RTT is cheap - the proximity is itself a quality win, and much of that
        // jitter is anycast-endpoint ICMP handling, not path. Scale a give-back by how much closer peering
        // is than the median transit ASN, and bound it to jitter's OWN weighted share of the grade, so it
        // can only ever cancel jitter's cost - never loss, stability, or a real congestion penalty.
        if (transitReferenceRttMs is double tr && meanRtt is double ix && ix > 0
            && jitterScore is int js && js < 100 && overall is int baseOverall)
        {
            var advantage = tr / ix; // 2.0 => peering reaches the internet at half the transit RTT
            var alpha = ScoreCurve.Interpolate(advantage, (1.0, 0), (1.5, 0.25), (2.0, 0.45), (3.0, 0.6));
            if (alpha > 0)
            {
                var qualityWeight = _options.AsnLatencyStabilityWeight + _options.AsnJitterWeight
                    + _options.AsnLossWeight + _options.AsnCongestionWeight;
                var jitterShare = qualityWeight > 0 ? _options.AsnJitterWeight / qualityWeight : 0;
                var recovered = alpha * jitterShare * (100 - js);
                overall = Math.Min(100, (int)Math.Round(baseOverall + recovered));
                _logger?.LogDebug(
                    "ISP Health: IX Peering proximity absolution - peering {Ix:0.0} ms vs transit ~{Tr:0.0} ms ({Adv:0.0}x), alpha {A:0.00} -> +{Rec:0.0} pts ({Base} -> {Ov})",
                    ix, tr, advantage, alpha, recovered, baseOverall, overall);
            }
        }

        return new IspAsnHealth
        {
            AsnNumber = IxPeeringAsn,
            AsnName = "IX Peering",
            TargetIds = peeringReached.SelectMany(d => d.TargetIds).Distinct().ToList(),
            MedianRttMs = AvgOf(p => p.MedianRttMs),
            MeanRttMs = meanRtt,
            P95RttMs = AvgOf(p => p.P95RttMs),
            MedianJitterMs = AvgOf(p => p.MedianJitterMs),
            ScoredJitterMs = AvgOf(p => p.ScoredJitterMs),
            LossPct = AvgOf(p => p.LossPct),
            ReachDeltaMs = AvgOf(p => p.ReachDeltaMs),
            RawJitterMs = AvgOf(p => p.RawJitterMs),
            LatencyStabilityScore = AvgIntOf(p => p.LatencyStabilityScore),
            JitterScore = jitterScore,
            LossScore = AvgIntOf(p => p.LossScore),
            ReachLatencyScore = AvgIntOf(p => p.ReachLatencyScore),
            CongestionScore = AvgIntOf(p => p.CongestionScore),
            // Average of the per-member grades (one flapping peer moves it 1/N), then lifted by the
            // proximity absolution above.
            OverallScore = overall,
            CongestionEventCount = perPeer.Sum(p => p.CongestionEventCount)
        };
    }

    private List<IspAsnHealth> GradeIspHops(
        List<AsnSeries> ispHopSeries,
        List<AsnSeries> transitSeries,
        List<IspAsnHealth> transitAsns,
        List<AsnSeries> destinationSeries,
        List<CongestionEvent> congestionEvents,
        double? jitterFloorMs,
        bool hopOrderKnown,
        double? accessBaselineRtt,
        double? internetMedianDeltaMs)
    {
        // Transit witnesses: each transit ASN's ancestor IPs + its effective jitter.
        var transitJitterByAsn = transitAsns
            .Where(a => a.ScoredJitterMs.HasValue)
            .GroupBy(a => a.AsnNumber)
            .ToDictionary(g => g.Key, g => g.Min(a => a.ScoredJitterMs!.Value));
        var transitWitnesses = transitSeries
            .Where(s => transitJitterByAsn.ContainsKey(s.AsnNumber))
            .Select(s => (Ancestors: s.AncestorIps, Jitter: transitJitterByAsn[s.AsnNumber]))
            .ToList();

        // Destination witnesses: each monitored endpoint's ancestor IPs + its end-to-end
        // jitter. Always strict (routes-through required) - a destination's clean path says
        // nothing about a hop it doesn't cross, so it never absolves on faith. Only built when
        // ancestry exists; without it (hopOrderKnown false) destinations can never absolve, so
        // we skip computing their jitter entirely.
        var destinationWitnesses = hopOrderKnown
            ? destinationSeries
                .Select(s => (s.AncestorIps, Jitter: ScoringJitterOf(s.Samples)))
                .Where(w => w.Jitter.HasValue)
                .Select(w => (w.AncestorIps, Jitter: w.Jitter!.Value))
                .ToList()
            : new List<(List<string> AncestorIps, double Jitter)>();

        // ISP hop witnesses: each hop series + its own measured jitter.
        var ispHopJitter = ispHopSeries
            .Select(s => (Series: s, Jitter: ScoringJitterOf(s.Samples)))
            .ToList();

        // Stability witnesses (parallel to the jitter witnesses above): the same three sources,
        // each carrying its absolute RTT MAD. A witness proven to route through a hop bounds the
        // hop's true RTT wander - a steadier forwarded path proves the hop's own MAD is a per-hop
        // artifact (ICMP-deprioritized control plane). Transit gate opens without hop order (always
        // downstream); destinations and ISP siblings stay strict.
        var transitMadWitnesses = transitSeries
            .Select(s => (Ancestors: s.AncestorIps, Mad: RttMadOf(s.Samples)))
            .Where(w => w.Mad.HasValue)
            .Select(w => (w.Ancestors, Mad: w.Mad!.Value))
            .ToList();
        var destinationMadWitnesses = hopOrderKnown
            ? destinationSeries
                .Select(s => (s.AncestorIps, Mad: RttMadOf(s.Samples)))
                .Where(w => w.Mad.HasValue)
                .Select(w => (w.AncestorIps, Mad: w.Mad!.Value))
                .ToList()
            : new List<(List<string> AncestorIps, double Mad)>();
        var ispHopMad = ispHopSeries
            .Select(s => (Series: s, Mad: RttMadOf(s.Samples)))
            .ToList();

        var grades = new List<IspAsnHealth>();
        foreach (var asnGroup in ispHopSeries.GroupBy(s => s.AsnNumber))
        {
            var hops = asnGroup.ToList();
            var floorRtt = hops
                .Select(s => SeriesStats.Median(s.Samples.Where(x => x.RttAvgMs.HasValue).Select(x => x.RttAvgMs!.Value).ToList()))
                .Where(m => m.HasValue)
                .Select(m => m!.Value)
                .DefaultIfEmpty()
                .Min();
            double? intraFloor = hops.Any(s => s.Samples.Any(x => x.RttAvgMs.HasValue)) ? floorRtt : null;
            foreach (var hop in hops)
            {
                var measured = ScoringJitterOf(hop.Samples);
                // Transit is always downstream of the ISP: with ancestor data we require a
                // proven routes-through (this hop is in the transit's ancestor set), without
                // it the gate is open. ISP siblings are strict either way - a sibling absolves
                // only a hop in its ancestor set, never on faith.
                var witnesses = transitWitnesses
                    .Where(w => !hopOrderKnown || RoutesThrough(w.Ancestors, hop.HopIps))
                    .Select(w => w.Jitter)
                    .Concat(ispHopJitter
                        .Where(h => hopOrderKnown && !ReferenceEquals(h.Series, hop) && h.Jitter.HasValue
                            && RoutesThrough(h.Series.AncestorIps, hop.HopIps))
                        .Select(h => h.Jitter!.Value))
                    .Concat(destinationWitnesses
                        .Where(w => hopOrderKnown && RoutesThrough(w.AncestorIps, hop.HopIps))
                        .Select(w => w.Jitter))
                    .ToList();
                double? effective = measured;
                if (witnesses.Count > 0)
                    effective = measured.HasValue ? Math.Min(measured.Value, witnesses.Min()) : witnesses.Min();

                // Same routes-through gate, in absolute RTT MAD, for the hop's stability.
                var stabWitnesses = transitMadWitnesses
                    .Where(w => !hopOrderKnown || RoutesThrough(w.Ancestors, hop.HopIps))
                    .Select(w => w.Mad)
                    .Concat(ispHopMad
                        .Where(h => hopOrderKnown && !ReferenceEquals(h.Series, hop) && h.Mad.HasValue
                            && RoutesThrough(h.Series.AncestorIps, hop.HopIps))
                        .Select(h => h.Mad!.Value))
                    .Concat(destinationMadWitnesses
                        .Where(w => hopOrderKnown && RoutesThrough(w.AncestorIps, hop.HopIps))
                        .Select(w => w.Mad))
                    .ToList();
                double? stabOverride = stabWitnesses.Count > 0 ? stabWitnesses.Min() : (double?)null;

                var grade = GradeAsn(hop, congestionEvents, jitterFloorMs, accessBaselineRtt, internetMedianDeltaMs,
                    intraAsnFloorRttMs: intraFloor, jitterOverrideMs: effective, stabilityMadOverrideMs: stabOverride);
                // Log the graded effective (post sub-0.05 ms assimilation snap in GradeAsn), not the
                // raw witness min, so the log matches what the hop is actually scored on. Stability is
                // logged alongside jitter: its own measured RTT MAD, the witness-floor it was absolved
                // to (n/a when no routes-through witness), the witness count, and the resulting score.
                _logger?.LogDebug(
                    "ISP Health: ISP hop {Target} (AS{Asn}) graded {Score} - jitter measured {Jitter} ms -> effective {Eff} ms ({JWit} witnesses); stability MAD measured {Mad} ms -> witness-floor {StabWit} ms ({SWit} witnesses) score {StabScore}; reach +{Reach} ms",
                    hop.TargetIds.FirstOrDefault(), hop.AsnNumber, grade.OverallScore,
                    FormatMsOrNull(measured), FormatMsOrNull(grade.ScoredJitterMs), witnesses.Count,
                    FormatMsOrNull(RttMadOf(hop.Samples)), FormatMsOrNull(stabOverride), stabWitnesses.Count, grade.LatencyStabilityScore,
                    FormatMsOrNull(grade.ReachDeltaMs));
                grades.Add(grade);
            }
        }
        return grades;
    }

    /// <summary>
    /// Whether a witness routes through a hop (and so may absolve it): the hop's IP must be in
    /// the witness's ancestor set - proven upstream of the witness on a shared discovery trace.
    /// </summary>
    private static bool RoutesThrough(List<string> witnessAncestors, List<string> hopIps) =>
        hopIps.Any(ip => witnessAncestors.Contains(ip, StringComparer.OrdinalIgnoreCase));

    /// <summary>Total hours covered by the union of the events' time windows (overlaps counted once).</summary>
    private static double UnionHours(IReadOnlyList<CongestionEvent> events)
    {
        double total = 0;
        DateTime curStart = default, curEnd = default;
        var open = false;
        foreach (var e in events.OrderBy(e => e.Start))
        {
            if (!open) { curStart = e.Start; curEnd = e.End; open = true; }
            else if (e.Start > curEnd) { total += (curEnd - curStart).TotalHours; curStart = e.Start; curEnd = e.End; }
            else if (e.End > curEnd) curEnd = e.End;
        }
        if (open) total += (curEnd - curStart).TotalHours;
        return total;
    }

    /// <summary>
    /// Collapses per-hop ISP grades to one entry per ASN for the Networks on Your Path
    /// card: mean RTT and jitter across the hops, averaged grade, and the union of the
    /// ASN's congestion events.
    /// </summary>
    private static List<IspAsnHealth> AggregateIspAsns(List<IspAsnHealth> hopGrades, List<CongestionEvent> congestionEvents, double assimilationMinDeltaMs)
    {
        var result = new List<IspAsnHealth>();
        foreach (var group in hopGrades.GroupBy(h => h.AsnNumber))
        {
            var hops = group.ToList();
            var targetIds = hops.SelectMany(h => h.TargetIds).Distinct().ToList();
            var targetSet = new HashSet<string>(targetIds);
            var asnEvents = congestionEvents
                .Where(e => e.Disposition == CongestionDisposition.Confirmed
                    && e.AsnNumbers.Contains(group.Key)
                    && (e.TargetIds.Count == 0 || e.TargetIds.Any(t => targetSet.Contains(t))))
                .ToList();
            var means = hops.Select(h => h.MeanRttMs).Where(m => m.HasValue).Select(m => m!.Value).ToList();
            // Each hop's ScoredJitterMs is its per-hop effective (absolved) jitter; RawJitterMs
            // is its own measured reading. The card shows the mean effective and flags
            // assimilation when that fell below the mean measured.
            var effJitters = hops.Select(h => h.ScoredJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            var rawJitters = hops.Select(h => h.RawJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            double? effMean = effJitters.Count > 0 ? effJitters.Average() : null;
            double? rawMean = rawJitters.Count > 0 ? rawJitters.Average() : null;
            var lossVals = hops.Select(h => h.LossPct).Where(l => l.HasValue).Select(l => l!.Value).ToList();
            var medianRtts = hops.Select(h => h.MedianRttMs).Where(m => m.HasValue).Select(m => m!.Value).ToList();
            var scored = hops.Where(h => h.OverallScore.HasValue).Select(h => h.OverallScore!.Value).ToList();
            result.Add(new IspAsnHealth
            {
                AsnNumber = group.Key,
                AsnName = hops.Select(h => h.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                TargetIds = targetIds,
                MedianRttMs = medianRtts.Count > 0 ? medianRtts.Min() : null,
                MeanRttMs = means.Count > 0 ? means.Average() : null,
                // RTT range across the ISP hops, on the same winsorized mean the hops display.
                MinRttMs = means.Count > 0 ? means.Min() : null,
                MaxRttMs = means.Count > 0 ? means.Max() : null,
                ScoredJitterMs = effMean,
                LossPct = lossVals.Count > 0 ? lossVals.Average() : null,
                OverallScore = scored.Count > 0 ? (int)Math.Round(scored.Average()) : null,
                CongestionEventCount = asnEvents.Count,
                JitterAssimilated = effMean.HasValue && rawMean.HasValue && effMean.Value < rawMean.Value - assimilationMinDeltaMs,
                RawJitterMs = rawMean
            });
        }
        return result;
    }

    /// <summary>
    /// A hop is suggested for disable when its jitter score is at or below this - i.e. jitter is a
    /// real deficit, not noise. Combined with "off the traced path" to avoid nagging on clean hops.
    /// </summary>
    private const int DisableSuggestMaxJitterScore = 70;

    private IspTargetHealth BuildIspTargetHealth(AsnSeries series, string? firstHopTargetId, List<IspAsnHealth> hopGrades, double winsorPercentile, IReadOnlySet<string> notTracedTargetIds, IReadOnlyDictionary<string, string> targetAddresses)
    {
        var rtts = series.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var jitters = series.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        var losses = series.Samples.Where(s => s.LossPercent.HasValue && !InOutage(s.Time)).Select(s => s.LossPercent!.Value).ToList();
        var targetId = series.TargetIds.FirstOrDefault() ?? "";
        var grade = hopGrades.FirstOrDefault(g => g.TargetIds.Contains(targetId));
        // Jitter comes from the grade (the effective/absolved value the hop is scored on), so
        // the row matches the grade beside it. Fall back to the hop's own raw jitter percentile when ungraded.
        var rawScored = jitters.Count > 0 ? SeriesStats.Percentile(jitters, _options.AsnJitterScoringPercentile) : null;
        var isGraded = targetId == firstHopTargetId;
        var notTraced = notTracedTargetIds.Contains(targetId);
        return new IspTargetHealth
        {
            TargetId = targetId,
            Name = series.AsnName ?? targetId,
            Address = targetAddresses.GetValueOrDefault(targetId),
            RttMs = SeriesStats.WinsorizedMean(rtts, winsorPercentile),
            ScoredJitterMs = grade?.ScoredJitterMs ?? rawScored,
            RawJitterMs = grade?.RawJitterMs ?? rawScored,
            JitterAssimilated = grade?.JitterAssimilated ?? false,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            OverallScore = grade?.OverallScore,
            ReachDeltaMs = grade?.ReachDeltaMs,
            IsGradedHop = isGraded,
            NotOnTracedPath = notTraced,
            // Off the traced path AND its jitter is a real deficit - the "why am I still scoring this?"
            // candidate. Never the graded nearest hop.
            SuggestDisable = notTraced && !isGraded && grade?.JitterScore is int js && js <= DisableSuggestMaxJitterScore
        };
    }

    /// <summary>The ISP Network dimension: averages every ISP hop grade. The per-hop
    /// detail is rendered from <see cref="IspHealthReport.IspTargets"/>, so the dimension
    /// itself carries no factors.</summary>
    private IspScoreDimension BuildIspDimension(double weight, List<IspAsnHealth> hopGrades)
    {
        var scored = hopGrades.Where(h => h.OverallScore.HasValue).Select(h => h.OverallScore!.Value).ToList();
        int? score = scored.Count > 0 ? (int)Math.Round(scored.Average()) : null;
        return new IspScoreDimension { Name = "ISP Network", Score = score, Weight = weight, Factors = new List<IspScoreFactor>() };
    }

    private static IspScoreDimension BuildDimension(string name, double weight, List<IspScoreFactor> factors)
    {
        var scored = factors.Where(f => f.Score.HasValue).ToList();
        int? score = null;
        if (scored.Count > 0)
        {
            var totalWeight = scored.Sum(f => f.Weight);
            score = (int)Math.Round(scored.Sum(f => f.Score!.Value * f.Weight) / totalWeight);
        }
        return new IspScoreDimension { Name = name, Score = score, Weight = weight, Factors = factors };
    }

    /// <summary>
    /// Weight below which a graded transit ASN never falls: the least-involved transit still counts
    /// at 25% relative to the most-involved (a 4x cap on the spread), so a bad-but-minor transit is
    /// de-weighted but never escapes accountability. Arm 4.
    /// </summary>
    private const double TransitInvolvementFloor = 0.25;

    /// <summary>
    /// A monitored internet destination is treated as reached over the access ISP's own peering/IX
    /// (rather than a transit provider) when BOTH hold: its forward path crosses no transit ASN, and
    /// its best-case (min) RTT delta beyond the first clean ISP hop is under this. The AS-path arm
    /// alone would count a low-latency destination that still traverses transit (e.g. a nearby transit
    /// PoP); the latency arm alone would count a peer sitting behind a long hidden L2 haul (an IX-local
    /// AS-path that's still +15 ms). Both together isolate genuine direct peering. It's a delta beyond
    /// the access hop, so the access technology's own latency is already subtracted and the same
    /// threshold holds for fiber, cable, cellular, or satellite.
    /// </summary>
    private const double IxPeeringMaxBestCaseDeltaMs = 10.0;

    /// <summary>Synthetic ASN number for the <see cref="GradeIxPeering"/> entry. Negative so every
    /// display path gated on <c>AsnNumber &gt; 0</c> (the "· ASn" suffix, BGP toolkit links) skips it.</summary>
    private const int IxPeeringAsn = -1;

    /// <summary>
    /// Builds a transit-style ASN dimension: a plain involvement-weighted average of the entries' own
    /// scores. Each carries its <see cref="IspAsnHealth.InvolvementWeight"/> (0.25-1.0, set upstream from
    /// internet-host involvement), so the networks you actually use dominate and a side-path transit
    /// counts only lightly - but its REAL score, never a neutral fill. The dimension therefore always
    /// lands within the range of its entries (no synthetic baseline can lift it above every one), and a
    /// congested off-path transit still dings it a little rather than being masked toward 100 - it may be
    /// on your return path or a failover route. When no ASN has involvement set (no attribution), it's
    /// the plain average and no fraction icon is shown.
    /// </summary>
    private static IspScoreDimension BuildAsnDimension(string name, double weight, List<IspAsnHealth> asns)
    {
        var factors = asns.Select(a => new IspScoreFactor
        {
            Name = string.IsNullOrEmpty(a.AsnName) ? $"AS{a.AsnNumber}" : a.AsnName,
            Score = a.OverallScore,
            Weight = a.InvolvementWeight ?? 1.0,
            ValueText = a.MeanRttMs.HasValue ? FormatMsCoarse(a.MeanRttMs.Value) : null,
            Description = a.CongestionEventCount > 0
                ? $"{a.CongestionEventCount} congestion event{(a.CongestionEventCount == 1 ? "" : "s")} in the window."
                : null,
            InvolvementTooltip = a.InvolvementTooltip,
            LowReachScoreCaveat = a.LowReachScoreCaveat
        }).ToList();

        var scored = asns.Where(a => a.OverallScore.HasValue).ToList();
        int? score;
        if (scored.Count == 0)
            score = null;
        else if (scored.All(a => a.InvolvementWeight is null))
            score = (int)Math.Round(scored.Average(a => a.OverallScore!.Value));
        else
        {
            var wsum = scored.Sum(a => a.InvolvementWeight ?? 1.0);
            score = wsum > 0
                ? (int)Math.Round(scored.Sum(a => (a.InvolvementWeight ?? 1.0) * a.OverallScore!.Value) / wsum)
                : (int)Math.Round(scored.Average(a => a.OverallScore!.Value));
        }
        return new IspScoreDimension { Name = name, Score = score, Weight = weight, Factors = factors };
    }

    private int CombineDimensions(params IspScoreDimension[] dimensions)
    {
        var scored = dimensions.Where(d => d.Score.HasValue).ToList();
        if (scored.Count == 0) return 0;
        var totalWeight = scored.Sum(d => d.Weight);
        return (int)Math.Round(scored.Sum(d => d.Score!.Value * d.Weight) / totalWeight);
    }

    private List<IspHealthIssue> CollectIssues(
        IspHealthInputs inputs,
        AccessProfile profile,
        IspHealthReport report,
        Dictionary<DateTime, LoadWindow> loadWindows,
        LoadedDeltas loadedDeltas)
    {
        var issues = new List<IspHealthIssue>();

        // Local (LAN/gateway) outages are surfaced in the waterfall but are not internet outages and
        // don't affect the score, so they never appear in this ISP-impact issue.
        // Full outages and brief disruptions are surfaced as separate findings: a 30 s transit flap
        // shouldn't read as the same event as a multi-minute outage. Both ride the same duration and
        // occurrence terms (a brief disruption costs about a point on its own), so the per-event score
        // share is taken from the already-attributed ScorePenaltyPoints rather than re-deriving it here.
        // Acknowledged ("that was me") outages were the user's own doing and are left out of the
        // findings entirely, same as the penalty.
        var wanOutages = inputs.Outages.Where(o => o.Scope != OutageScope.Local && !o.Acknowledged).ToList();
        var fullOutages = wanOutages.Where(o => !o.IsPartial && !o.IsBrief).ToList();
        var briefDisruptions = wanOutages.Where(o => !o.IsPartial && o.IsBrief).ToList();
        var partialDisruptions = wanOutages.Where(o => o.IsPartial).ToList();
        if (fullOutages.Count > 0)
        {
            var multiple = fullOutages.Count > 1;
            var totalDown = TimeSpan.FromMinutes(fullOutages.Sum(o => o.Duration.TotalMinutes));
            var upstream = fullOutages.Where(o => o.Scope == OutageScope.Upstream && !string.IsNullOrEmpty(o.LastReachableHop)).ToList();
            var allUpstream = fullOutages.All(o => o.Scope == OutageScope.Upstream) && upstream.Count > 0;
            var where = allUpstream
                ? $" The break sat upstream of {string.Join(", ", upstream.Select(o => o.LastReachableHop).Distinct())} - your equipment stayed reachable, so {(multiple ? "these were" : "this was")} an ISP-side fault, not your network."
                : " At least one event took the whole WAN dark, including the first ISP hop.";
            var count = multiple
                ? $"{fullOutages.Count} internet outages totaling {FormatOutageDuration(totalDown)}"
                : $"An internet outage of {FormatOutageDuration(fullOutages[0].Duration)}";
            // Be transparent about the score hit: the outage penalty is applied at the top level
            // and isn't tied to any one factor, so spell it out here or it's invisible.
            var penalty = fullOutages.Sum(o => o.ScorePenaltyPoints);
            var impact = penalty > 0
                ? $" {(multiple ? "Together they" : "It")} lowered your ISP Health score by {penalty} {(penalty == 1 ? "point" : "points")}."
                : string.Empty;
            // The score is graded on downtime as a FRACTION of the window, so state the availability
            // that produced it - "15 min" alone reads the same on a 48 h window and a 30 day one, and
            // those are wildly different lines. Suppressed on short windows, where a percentage
            // invites being read as a monthly SLA figure when it covers a few hours.
            var rate = multiple ? DropRatePhrase(fullOutages.Count, (inputs.WindowEnd - inputs.WindowStart).TotalHours) : string.Empty;
            var uptime = (inputs.WindowEnd - inputs.WindowStart).TotalHours >= _options.UptimeProseMinWindowHours
                ? $" That is {IspHealthPresentation.FormatUptime(report)} uptime across the last {IspHealthPresentation.ProseWindowLabel(report)}{(rate.Length > 0 ? $", {rate}" : "")}."
                : string.Empty;
            var realPhrase = multiple
                ? "so these are real outages, not monitoring gaps"
                : "so this is a real outage, not a monitoring gap";
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = multiple ? "Internet outages in the window" : "Internet outage in the window",
                Description = $"{count} occurred while the Monitoring Agent kept probing ({realPhrase}).{where}{uptime}{impact}{UsageNote(fullOutages)}",
                Recommendation = allUpstream
                    ? "No action needed on your side for an upstream outage; it is logged here so you can correlate it with ISP incidents."
                    : "Logged here so you can correlate it with ISP incidents; if the first ISP hop keeps dropping, check your modem/ONT and the line to your ISP.",
                LinkUrl = "#isp-outages",
                LinkText = "The recovery shape is shown on the timeline below.",
                OutageStarts = fullOutages.Select(o => o.Start).ToList()
            });
        }
        if (briefDisruptions.Count > 0)
        {
            var multiple = briefDisruptions.Count > 1;
            var totalDown = TimeSpan.FromSeconds(briefDisruptions.Sum(o => o.Duration.TotalSeconds));
            var upstream = briefDisruptions.Where(o => o.Scope == OutageScope.Upstream && !string.IsNullOrEmpty(o.LastReachableHop)).ToList();
            var allUpstream = briefDisruptions.All(o => o.Scope == OutageScope.Upstream) && upstream.Count > 0;
            var count = multiple
                ? $"{briefDisruptions.Count} brief internet disruptions totaling {FormatBriefDuration(totalDown)}"
                : $"A brief internet disruption of {FormatBriefDuration(briefDisruptions[0].Duration)}";
            var where = allUpstream
                ? $" {(multiple ? "They sat" : "It sat")} upstream of {string.Join(", ", upstream.Select(o => o.LastReachableHop).Distinct())}, so your equipment stayed reachable - short ISP-side flaps."
                : string.Empty;
            var penalty = briefDisruptions.Sum(o => o.ScorePenaltyPoints);
            var impact = penalty > 0
                ? $" {(multiple ? "Together they" : "It")} lowered your ISP Health score by {penalty} {(penalty == 1 ? "point" : "points")}."
                : " Too short to meaningfully affect your score; logged for visibility.";
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = multiple ? "Brief internet disruptions in the window" : "Brief internet disruption in the window",
                Description = $"{count} occurred while the Monitoring Agent kept probing (so {(multiple ? "these are real, not monitoring gaps" : "this is real, not a monitoring gap")}).{where}{impact}{UsageNote(briefDisruptions)}",
                Recommendation = "Short drops like these are usually transient upstream or transit events; logged here so you can spot a pattern of flapping.",
                LinkUrl = "#isp-outages",
                LinkText = "Shown on the timeline below.",
                OutageStarts = briefDisruptions.Select(o => o.Start).ToList()
            });
        }
        if (partialDisruptions.Count > 0)
        {
            var multiple = partialDisruptions.Count > 1;
            var totalDown = TimeSpan.FromSeconds(partialDisruptions.Sum(o => o.Duration.TotalSeconds));
            var worst = partialDisruptions.OrderByDescending(o => o.PeakLossPct).First();
            var penalty = partialDisruptions.Sum(o => o.ScorePenaltyPoints);
            var count = multiple
                ? $"{partialDisruptions.Count} partial-loss disruptions totaling {FormatBriefDuration(totalDown)}"
                : $"A partial-loss disruption of {FormatBriefDuration(partialDisruptions[0].Duration)}";
            var breadth = $" Peak loss reached {worst.PeakLossPct:0}% across {worst.DegradedTargetCount} of {worst.PathTargetCount} path targets, so the loss was widespread rather than one bad target.";
            var impact = $" {(multiple ? "Together they" : "It")} lowered your ISP Health score by {penalty} {(penalty == 1 ? "point" : "points")}.";
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = multiple ? "Partial-loss disruptions in the window" : "Partial-loss disruption in the window",
                Description = $"{count} hit the path: many targets degraded at once without going fully dark, so the internet was lossy but not unreachable.{breadth}{impact}{UsageNote(partialDisruptions)}",
                Recommendation = "Coincident partial loss across many targets is usually upstream/transit congestion or a brief routing wobble; logged so you can correlate it with ISP incidents or watch for a pattern.",
                LinkUrl = "#isp-outages",
                LinkText = "Shown on the timeline below."
                // No "that was me" here: partial loss is congestion/routing behavior, not the
                // signature of the user's own maintenance (that reads as a blackout). The
                // per-event action on the disruption rows still allows excluding one.
            });
        }

        if (!report.HasExpectedSpeeds)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Expected ISP speeds not set",
                Description = "Loaded-line analysis is skipped because no ISP speeds are configured.",
                Recommendation = "Set your ISP download and upload speeds in UniFi Network (Settings, Internet, your WAN) so ISP Health can grade behavior under load."
            });
        }

        var (latencyTriggered, lossTriggered) = SqmTriggers(inputs, profile, loadWindows, loadedDeltas);
        if (latencyTriggered || lossTriggered)
        {
            // Adaptive SQM (our feature) overrides the UniFi Smart Queues messaging: we can see
            // it's already shaping this WAN, so don't pitch it or tell the user to enable Smart
            // Queues. Loss under load while it shapes means the rate it holds isn't backing off
            // enough for the real-time capacity drop, so point at its own tuning knobs (Severity
            // deepens the time-of-day dips; nominal speeds set the ceiling everything scales from).
            string recommendation;
            if (inputs.AdaptiveSqmEnabled)
            {
                recommendation = "Adaptive SQM is already shaping this WAN, so loss under load means the rate it holds isn't backing off enough when the line congests. In your Adaptive SQM settings, raise the Severity so the peak-hour rate dips go deeper, or lower the nominal download/upload if the line consistently delivers less than its plan. If loss persists once the rate is pulled down, the drops are upstream and only your ISP can fix them.";
            }
            else if (inputs.SmartQueuesEnabled)
            {
                recommendation = "Smart Queues is enabled on this WAN but the line still degrades under load; check that its configured rates match what the line actually delivers.";
            }
            else
            {
                recommendation = "Enable Smart Queues (SQM) on this WAN in UniFi Network (Settings, Internet, your WAN, Smart Queues).";
            }
            // Only pitch Adaptive SQM when the WAN isn't already running it.
            if (!inputs.AdaptiveSqmEnabled
                && inputs.CongestionEvents.Count(e => e.Disposition == CongestionDisposition.Confirmed) >= _options.SqmRecurringCongestionEvents)
            {
                recommendation += " This connection also shows a recurring congestion pattern; consider Adaptive SQM, which tracks time-of-day capacity changes automatically.";
            }
            if (latencyTriggered)
            {
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Bufferbloat under load",
                    Description = "Latency rises well beyond the excellent range for this connection type when the line is loaded.",
                    Recommendation = recommendation,
                    LinkUrl = "/sqm",
                    LinkText = "Adaptive SQM"
                });
            }
            if (lossTriggered)
            {
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Packet loss under load",
                    Description = "Packet loss exceeds the acceptable band for this connection type when the line is loaded.",
                    Recommendation = recommendation,
                    LinkUrl = "/sqm",
                    LinkText = "Adaptive SQM",
                    InvestigateUrl = "/monitoring?tab=performance&investigate=loaded-loss",
                    InvestigateText = "Investigate on the charts"
                });
            }
        }

        var speedFactor = report.AccessDimension.Factors.FirstOrDefault(f => f.Name == "Speed vs Plan");
        if (speedFactor?.Score is < 70)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Throughput below plan",
                Description = $"The best WAN speed test ({speedFactor.ValueText}) falls well short of the {FormatMbps(inputs.ExpectedDownloadMbps ?? 0)} / {FormatMbps(inputs.ExpectedUploadMbps ?? 0)} Mbps plan configured in UniFi Network.",
                Recommendation = "If the configured plan speeds are right, raise the shortfall with your ISP. If the plan changed, update the ISP speeds in UniFi Network so grading stays accurate."
            });
        }

        var idleLatencyFactor = report.AccessDimension.Factors.FirstOrDefault(f => f.Name == "Idle Latency");
        if (idleLatencyFactor?.Score is < 75)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Idle latency above normal",
                Description = $"Baseline first-hop latency of {idleLatencyFactor.ValueText} is above the normal range for {profile.DisplayName}.",
                Recommendation = "Common causes: access layer congestion or overprovisioning by the ISP, CPE inefficiency (try a reboot or firmware update), or a longer-than-expected physical haul to the first hop."
            });
        }

        var packetLossFactor = report.AccessDimension.Factors.FirstOrDefault(f => f.Name == "Packet Loss");
        if (packetLossFactor?.Score is < 70)
        {
            // Dedicated point-to-point media (DSL pair, Active Ethernet / DIA) have no
            // contended segment, so persistent loss there is a physical-plant fault. On
            // shared media the same loss can equally be an oversubscribed segment upstream,
            // and on the neutral PPPoE/Other profile the medium is unknown so we hedge.
            string lossRecommendation;
            if (!profile.SharedMedium)
            {
                lossRecommendation = "Persistent loss regardless of load usually points at the physical layer: check optics, connectors, or line/signal levels, and raise it with your ISP.";
            }
            else if (profile.IsNeutral)
            {
                lossRecommendation = "Persistent loss regardless of load points at the access layer: a physical-plant fault (optics, connectors, coax fittings, or signal levels), or - if your line runs over a shared medium like cable, PON, fixed wireless, or cellular - an oversubscribed segment carrying too many subscribers. Raise it with your ISP either way.";
            }
            else
            {
                lossRecommendation = $"Persistent loss regardless of load points at the access layer: a physical-plant fault (optics, connectors, coax fittings, or signal levels), or an oversubscribed segment upstream, since {profile.DisplayName} shares capacity across subscribers. Raise it with your ISP.";
            }

            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Packet loss above acceptable",
                Description = $"Average packet loss of {packetLossFactor.ValueText} exceeds the {FormatPct(profile.IdleLossAcceptablePct)} acceptable ceiling for {profile.DisplayName}.",
                Recommendation = lossRecommendation,
                InvestigateUrl = "/monitoring?tab=performance&investigate=packet-loss",
                InvestigateText = "Investigate on the charts"
            });
        }

        var sharedEvents = inputs.CongestionEvents.Where(e => e.IsShared).ToList();
        if (sharedEvents.Count > 0)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Shared upstream congestion",
                Description = $"{sharedEvents.Count} congestion event{(sharedEvents.Count == 1 ? "" : "s")} hit multiple networks at once, which usually means a shared upstream or return path is the bottleneck rather than the individual networks shown."
            });
        }

        return issues;
    }

    private (bool Latency, bool Loss) SqmTriggers(
        IspHealthInputs inputs,
        AccessProfile profile,
        Dictionary<DateTime, LoadWindow> loadWindows,
        LoadedDeltas loadedDeltas)
    {
        var bandWidth = profile.LoadedDeltaAcceptableMs - profile.LoadedDeltaExcellentMs;
        var deltaThreshold = profile.LoadedDeltaExcellentMs + _options.SqmDeviationFactor * bandWidth;
        var latency = loadedDeltas.DownMs > deltaThreshold || loadedDeltas.UpMs > deltaThreshold;

        var loss = false;
        if (loadWindows.Count > 0)
        {
            var downLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedDown, w => w.IsLoadedUp);
            var upLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedUp, w => w.IsLoadedDown);
            loss = downLoss > profile.LoadedLossDownHighPct || upLoss > profile.LoadedLossUpHighPct;
        }
        return (latency, loss);
    }

    private DateTime FloorToWindow(DateTime time) =>
        CongestionDetector.FloorTime(time, TimeSpan.FromSeconds(_options.LoadWindowSeconds));

    /// <summary>
    /// How often outages recurred, phrased for the findings: "about one drop a week". Picks the unit
    /// that keeps the count at or above one so a sparse month doesn't read as "about 0 drops a day".
    /// Empty when there is nothing to rate.
    /// </summary>
    private static string DropRatePhrase(int count, double windowHours)
    {
        if (count <= 0 || windowHours <= 0) return string.Empty;
        var perDay = count / (windowHours / 24.0);
        var (rate, unit) = perDay >= 1 ? (perDay, "day")
            : perDay * 7 >= 1 ? (perDay * 7, "week")
            : (perDay * 30, "month");
        var n = Math.Max(1, (int)Math.Round(rate));
        return n == 1 ? $"about one drop a {unit}" : $"about {n} drops a {unit}";
    }

    /// <summary>A cosmetic note for an outage finding whose events fell during typically-idle hours.
    /// The score impact already reflects the lower usage weight; this just explains why it's modest.
    /// Empty when usage weighting is off or the events landed during normal/heavy-usage hours.</summary>
    private string UsageNote(IReadOnlyCollection<OutageEvent> events)
    {
        if (!_options.UsageWeightingEnabled || events.Count == 0) return string.Empty;
        if (events.Min(e => e.UsageWeight) >= _options.UsageQuietWeightThreshold) return string.Empty;
        return events.Count > 1
            ? " Some fell while your connection is typically idle, so the real-world impact was likely lower than the downtime suggests."
            : " It fell while your connection is typically idle, so the real-world impact was likely lower than the downtime suggests.";
    }

    private static string FormatOutageDuration(TimeSpan d) =>
        d.TotalMinutes < 90 ? $"{d.TotalMinutes:0} min" : $"{d.TotalHours:0.#} h";

    private static string FormatBriefDuration(TimeSpan d) =>
        d.TotalSeconds < 90 ? $"{d.TotalSeconds:0} sec" : $"{d.TotalMinutes:0.#} min";

    private static string FormatMs(double ms) =>
        $"{ms.ToString("0.00", CultureInfo.InvariantCulture)} ms";

    /// <summary>Coarse RTT for dimension summaries: no decimals at or above 10 ms, one below.
    /// Detail lives on the Networks on Your Path cards.</summary>
    private static string FormatMsCoarse(double ms) =>
        ms >= 10 ? $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms" : $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";

    /// <summary>Band references and loaded deltas: one decimal (2.0 ms), not the value's two.</summary>
    private static string FormatMsBand(double ms) =>
        $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";

    /// <summary>Debug-log helper: a millisecond value to two decimals, or "n/a" when null.</summary>
    private static string FormatMsOrNull(double? ms) =>
        ms.HasValue ? ms.Value.ToString("0.00", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>Loaded-latency delta for display: a non-positive delta shows as "0 ms".</summary>
    private static string FormatLoadedDelta(double ms) => ms <= 0 ? "0 ms" : FormatMsBand(ms);

    private static string FormatPct(double pct) =>
        pct == 0 ? "0%" : $"{pct.ToString(pct < 0.1 ? "0.###" : "0.##", CultureInfo.InvariantCulture)}%";

    private static string FormatMbps(double mbps) =>
        mbps.ToString(mbps >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
}

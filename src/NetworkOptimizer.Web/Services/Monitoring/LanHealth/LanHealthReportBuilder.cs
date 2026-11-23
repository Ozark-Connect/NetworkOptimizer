using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Builds a LAN Health report from stored link hours: each link's evidence over the window is the
/// sum of its hours, its baseline the hours before, and the detectors and scorer run over that.
/// Pure, so the background roll-up, the live view and the tests share one path.
/// </summary>
public static class LanHealthReportBuilder
{
    /// <summary>Hours of history before the window that count as a link's own normal.</summary>
    public const int BaselineDays = 14;

    /// <summary>Exclusions that apply to a link (lanh-44).</summary>
    public static ExclusionLevel? ExclusionFor(LinkRecord link, IReadOnlyList<LanHealthExclusion> exclusions, bool scoreFabricOnly)
    {
        ExclusionLevel? level = null;
        void Take(int l) { var e = (ExclusionLevel)l; if (level == null || e > level) level = e; }
        foreach (var x in exclusions)
            if (Applies(link, x)) Take(x.Level);
        if (scoreFabricOnly && link.Kind == LinkKind.Access) Take((int)ExclusionLevel.ExcludeFromScore);
        // Fabric links can be muted but never excluded: a fabric fault hits everyone behind it.
        if (link.IsFabric && level == ExclusionLevel.ExcludeFromScore) level = ExclusionLevel.MuteAlerts;
        return level;
    }

    /// <summary>An SFP module on a link now: receive power, its best this week, and temperature.</summary>
    public sealed record SfpReading(double? RxDbm, double? RxBestDbm, double? TempC);

    /// <summary>
    /// The SFP reading for each link with a module at either end, from the live port tables and each module's best
    /// receive power this week (<paramref name="best"/>, keyed by device MAC and port index). Where both ends have
    /// one, the end receiving less light.
    /// </summary>
    public static Dictionary<string, SfpReading> SfpReadings(IReadOnlyList<LinkRecord> links, IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiDeviceResponse> devices,
        IReadOnlyDictionary<(string DeviceMac, string Port), double> best)
    {
        var ports = devices.Where(d => !string.IsNullOrEmpty(d.Mac))
            .SelectMany(d => (d.PortTable ?? []).Where(p => p.SfpFound == true).Select(p => (Mac: LanLinkBuilder.Mac(d.Mac), Port: p)))
            .GroupBy(x => (x.Mac, x.Port.PortIdx)).ToDictionary(g => g.Key, g => g.First().Port);
        var result = new Dictionary<string, SfpReading>();
        foreach (var l in links.Where(l => l.Kind == LinkKind.FabricWired))
        {
            SfpReading? End(LinkEnd e)
            {
                if (e.PortIdx is not int idx || !ports.TryGetValue((e.DeviceMac, idx), out var p)) return null;
                var key = (e.DeviceMac, idx.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return new SfpReading(p.SfpRxPower, best.TryGetValue(key, out var b) ? b : null, p.SfpTemperature);
            }
            var up = End(l.Upstream);
            var down = End(l.Downstream);
            var pick = up == null ? down : down == null ? up : (down.RxDbm ?? 0) < (up.RxDbm ?? 0) ? down : up;
            if (pick != null) result[l.Id] = pick;
        }
        return result;
    }

    /// <summary>Whether an exclusion covers the link: its port, its port profile, either device, or every access port.</summary>
    public static bool Applies(LinkRecord link, LanHealthExclusion x) => x.Scope switch
    {
        "port" => x.Key == link.Id,
        "device" => string.Equals(x.Key, link.Upstream.DeviceMac, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Key, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase),
        "profile" => !string.IsNullOrEmpty(link.UpstreamPort.PortProfileId) && x.Key == link.UpstreamPort.PortProfileId,
        "access-all" => link.Kind == LinkKind.Access,
        _ => false,
    };

    /// <summary>One link's evidence over a window, from its stored hours.</summary>
    public static LinkEvidence EvidenceFromRows(LinkRecord link, IReadOnlyList<LanHealthLinkHour> rows, IReadOnlyList<LanHealthLinkHour> baseline,
        DateTime from, DateTime to, int? historyDays, LinkRecord? parent = null,
        IReadOnlyList<LanHealthLinkHour>? parentRows = null, IReadOnlyList<LanHealthLinkHour>? parentBaseline = null, SfpReading? sfp = null)
    {
        var parentLoss = parentRows?.Where(r => r.ProbeLossPct != null).Select(r => r.ProbeLossPct!.Value).ToList();
        var (retryPct, attempts) = RetryPct(rows);
        var (retryBase, baseAttempts) = RetryPct(baseline);
        var (childRetry, _) = RetryPct(rows, child: true);
        var (childBase, childBaseAttempts) = RetryPct(baseline, child: true);
        // The median hour by distance from weak, so one bad hour does not stand for the window.
        var worst = rows.Where(r => r.MeshWorstSignalDbm != null && r.MeshWorstSignalRadio != null)
            .OrderBy(r => r.MeshWorstSignalDbm!.Value - NetworkOptimizer.WiFi.Helpers.SignalClassification.WeakBelowDbm(LanHealthDetectors.SignalBand(r.MeshWorstSignalRadio!)))
            .ToList() is { Count: > 0 } ws ? ws[ws.Count / 2] : null;
        double? Sum(Func<LanHealthLinkHour, double?> f) => rows.Any(r => f(r) != null) ? rows.Sum(r => f(r) ?? 0) : null;
        var upBlind = rows.Count > 0 && rows.All(r => r.UpErrorsBlind);
        var speeds = rows.Where(r => r.SpeedMinMbps is > 0)
            .SelectMany(r => new[] { new SpeedReading(r.HourStart, r.SpeedMinMbps!.Value, null, LinkSource.PortTableState),
                new SpeedReading(r.HourStart.AddMinutes(59), r.SpeedMaxMbps ?? r.SpeedMinMbps!.Value, null, LinkSource.PortTableState) })
            .ToList();
        var usual = UsualSpeed(link, baseline.Concat(rows));
        return new LinkEvidence
        {
            Link = link,
            From = from,
            To = to,
            Upstream = new EndCounters
            {
                RxErrors = upBlind ? null : Sum(r => r.UpRxErrors),
                TxErrors = Sum(r => r.UpTxErrors),
                TxDiscards = Sum(r => r.UpTxDiscards),
                RxPackets = Sum(r => r.UpRxPackets),
                TxPackets = Sum(r => r.UpTxPackets),
                LateCollisions = Sum(r => r.LateCollisions),
                StpChanges = Sum(r => r.StpChanges),
                ErrorsBlind = upBlind,
            },
            Downstream = new EndCounters
            {
                RxErrors = Sum(r => r.DownRxErrors),
                TxErrors = Sum(r => r.DownTxErrors),
                TxDiscards = Sum(r => r.DownTxDiscards),
                RxPackets = Sum(r => r.DownRxPackets),
                TxPackets = Sum(r => r.DownTxPackets),
            },
            Hours = rows.OrderBy(r => r.HourStart).Select(Sample).ToList(),
            BaselineHours = baseline.OrderBy(r => r.HourStart).Select(Sample).ToList(),
            Speeds = speeds,
            UsualSpeedMbps = usual > 0 ? usual : null,
            Mesh = rows.Where(r => r.MeshSignalDbm != null || r.MeshTxRateKbps != null)
                .Select(r => new MeshReading(r.HourStart, r.MeshSignalDbm, r.MeshTxRateKbps, r.MloMembers, r.ParentMac)).ToList(),
            MeshBaseline = baseline.Where(r => r.MeshSignalDbm != null || r.MeshTxRateKbps != null)
                .Select(r => new MeshReading(r.HourStart, r.MeshSignalDbm, r.MeshTxRateKbps, r.MloMembers, r.ParentMac)).ToList(),
            PoeDraw = rows.Where(r => r.PoeWattsMin != null).SelectMany(r => new[] { (r.HourStart, r.PoeWattsMin!.Value), (r.HourStart.AddMinutes(30), r.PoeWattsMax ?? r.PoeWattsMin!.Value) }).ToList(),
            ProbeLossPct = rows.Any(r => r.ProbeLossPct != null) ? rows.Where(r => r.ProbeLossPct != null).Average(r => r.ProbeLossPct!.Value) : null,
            ParentProbeLossPct = parentLoss is { Count: > 0 } ? parentLoss.Average() : null,
            ParentName = parent?.Downstream.DeviceName,
            RttGapMs = MedianGap(rows, parentRows, 1),
            RttGapBaselineMs = MedianGap(baseline, parentBaseline, 24),
            MeshRetryPct = retryPct,
            MeshTxAttempts = attempts,
            MeshRetryPctBaseline = baseAttempts >= MeshRetryMinAttempts * 4 ? retryBase : null,
            MeshTxDropped = rows.Sum(r => r.MeshTxDropped ?? 0),
            SfpRxPowerDbm = sfp?.RxDbm,
            SfpRxPowerBestDbm = sfp?.RxBestDbm,
            SfpTemperatureC = sfp?.TempC,
            MeshChildRetryPct = childRetry,
            MeshChildRetryPctBaseline = childBaseAttempts >= MeshRetryMinAttempts * 4 ? childBase : null,
            MeshChildTxDropped = rows.Sum(r => r.MeshChildTxDropped ?? 0),
            MeshWorstSignalDbm = worst?.MeshWorstSignalDbm,
            MeshWorstSignalRadio = worst?.MeshWorstSignalRadio,
            MeshWorstSignalAtParent = worst?.MeshWorstSignalAtParent == true,
            HistoryDays = historyDays,
        };
    }

    /// <summary>
    /// The fastest speed the link reached over its hours: its usual speed. A wired fabric link's id names both devices,
    /// so a new device is a new link; an access port keeps its id, so only the hours with today's client count
    /// (the link's name names the client), and a new client starts fresh.
    /// </summary>
    internal static int UsualSpeed(LinkRecord link, IEnumerable<LanHealthLinkHour> hours) =>
        hours.Where(r => link.Kind != LinkKind.Access || r.Name == null || r.Name == link.Name)
            .Select(r => r.SpeedMaxMbps).OfType<int>().DefaultIfEmpty().Max();

    /// <summary>Transmit attempts below which a retry rate is noise.</summary>
    internal const double MeshRetryMinAttempts = 100_000;

    /// <summary>Retries over attempts across the hours, percent, with the attempts behind it: the parent's side, or the child's.</summary>
    internal static (double? Pct, double Attempts) RetryPct(IReadOnlyList<LanHealthLinkHour> rows, bool child = false)
    {
        var hours = rows.Select(r => child ? (Retries: r.MeshChildTxRetries, Attempts: r.MeshChildTxAttempts) : (Retries: r.MeshTxRetries, Attempts: r.MeshTxAttempts))
            .Where(h => h.Attempts is > 0 && h.Retries != null).ToList();
        var attempts = hours.Sum(h => h.Attempts!.Value);
        return (attempts >= MeshRetryMinAttempts ? 100 * hours.Sum(h => h.Retries!.Value) / attempts : null, attempts);
    }

    /// <summary>
    /// Median of each hour's probe RTT minus the parent's in the same hour, over hours that have both (or the link's own
    /// RTT when there is no parent probe). Null below <paramref name="minHours"/> such hours.
    /// </summary>
    internal static double? MedianGap(IReadOnlyList<LanHealthLinkHour> rows, IReadOnlyList<LanHealthLinkHour>? parentRows, int minHours)
    {
        var parentByHour = (parentRows ?? []).Where(r => r.ProbeRttMs != null).GroupBy(r => r.HourStart).ToDictionary(g => g.Key, g => g.First().ProbeRttMs!.Value);
        var gaps = rows.Where(r => r.ProbeRttMs != null)
            .Select(r => parentRows == null ? r.ProbeRttMs : parentByHour.TryGetValue(r.HourStart, out var p) ? r.ProbeRttMs - p : null)
            .OfType<double>().OrderBy(v => v).ToList();
        return gaps.Count >= minHours ? gaps[gaps.Count / 2] : null;
    }

    private static LinkHourSample Sample(LanHealthLinkHour r) => new(r.HourStart, r.MaxInBps, r.MaxOutBps, r.MeanInBps, r.MeanOutBps,
        r.BroadcastPps, r.MulticastPps, r.PacketsPps, (r.UpTxDiscards ?? 0) + (r.DownTxDiscards ?? 0), (r.UpRxErrors ?? 0) + (r.DownRxErrors ?? 0),
        (r.UpTxPackets ?? 0) + (r.DownTxPackets ?? 0));

    /// <summary>
    /// Clean days of history before the first unexplained drop or loss burst in the window: how far
    /// back the stored hours go with none, so a finding can say "with none in the N days before".
    /// </summary>
    public static int? CleanDaysBefore(IReadOnlyList<LanHealthLinkHour> rows, IReadOnlyList<LanHealthLinkHour> baseline)
    {
        if (TroubleSince(rows, baseline) is not DateTime since || baseline.Count == 0) return null;
        var first = new DateTime(since.Year, since.Month, since.Day, since.Hour, 0, 0, DateTimeKind.Utc);
        var before = baseline.Where(r => r.HourStart < first).OrderByDescending(r => r.HourStart).ToList();
        if (before.Count == 0) return null;
        var dirty = before.FirstOrDefault(r => r.Drops + r.Relinks + r.LossBursts > 0);
        var cleanFrom = dirty?.HourStart ?? before[^1].HourStart;
        var days = (int)Math.Floor((first - cleanFrom).TotalDays);
        return days >= 1 ? days : null;
    }

    /// <summary>
    /// Each scored finding's cost to the LAN Health Score: the score with that one finding removed,
    /// less the score now. Findings combine multiplicatively, so the costs do not sum to the total.
    /// </summary>
    internal static void ScoreImpacts(LanHealthReport report)
    {
        if (report.Score is not double now) return;
        foreach (var l in report.Links)
        {
            var saved = l.Score;
            foreach (var f in l.Findings.Where(f => f.Points > 0))
            {
                l.Score = l.Exclusion == ExclusionLevel.ExcludeFromScore ? 100 : LanHealthScorer.LinkScore(l.Findings.Where(o => !ReferenceEquals(o, f)));
                f.ScoreImpact = Math.Max(0, (LanHealthScorer.SiteScore(report.Links) ?? now) - now);
            }
            l.Score = saved;
        }
    }

    /// <summary>A gap this long without a drop or loss burst ends a run of trouble.</summary>
    internal static readonly TimeSpan RunGap = TimeSpan.FromDays(3);

    private static bool Trouble(LanHealthLinkHour r) => r.Drops + r.Relinks + r.LossBursts > 0;

    /// <summary>
    /// When the link's current run of trouble began: from the first unexplained drop or loss burst in
    /// the window, back through the stored history (14 days) until a gap of RunGap with none.
    /// </summary>
    public static DateTime? TroubleSince(IReadOnlyList<LanHealthLinkHour> rows, IReadOnlyList<LanHealthLinkHour> baseline)
    {
        var start = rows.Where(Trouble).OrderBy(r => r.HourStart).FirstOrDefault();
        if (start == null) return null;
        foreach (var r in baseline.Where(r => Trouble(r) && r.HourStart < start.HourStart).OrderByDescending(r => r.HourStart))
        {
            if (start.HourStart - r.HourStart > RunGap) break;
            start = r;
        }
        return LanHealthIncidentJson.Deserialize(start.IncidentsJson).Where(i => !i.IsExplained).Select(i => (DateTime?)i.Start).Min()
            ?? start.HourStart;
    }

    /// <summary>The report over [from, to).</summary>
    public static LanHealthReport Build(
        IReadOnlyList<LinkRecord> links,
        IReadOnlyDictionary<string, List<LanHealthLinkHour>> rows,
        IReadOnlyDictionary<string, List<LanHealthLinkHour>> baseline,
        IReadOnlyList<LanHealthExclusion> exclusions,
        bool scoreFabricOnly,
        Func<IReadOnlyList<LinkHealth>, IReadOnlyDictionary<string, LinkEvidence>, SiteEvidence>? site,
        FindingText text,
        DateTime from, DateTime to,
        IReadOnlyDictionary<string, List<LinkSpeedTest>>? speedTests = null,
        IReadOnlyDictionary<string, SfpReading>? sfp = null)
    {
        var report = new LanHealthReport { From = from, To = to, Ready = rows.Count > 0 };
        var evidence = new Dictionary<string, LinkEvidence>();
        foreach (var link in links)
        {
            var linkRows = rows.GetValueOrDefault(link.Id) ?? [];
            var linkBase = baseline.GetValueOrDefault(link.Id) ?? [];
            var incidents = linkRows.SelectMany(r => LanHealthIncidentJson.Deserialize(r.IncidentsJson)).OrderBy(i => i.Start).ToList();
            // The fabric link into this link's upstream device: its probes separate this hop's loss and RTT from upstream.
            var parent = link.IsMesh ? links.FirstOrDefault(l => l.IsFabric && string.Equals(l.Downstream.DeviceMac, link.Upstream.DeviceMac, StringComparison.OrdinalIgnoreCase)) : null;
            var ev = EvidenceFromRows(link, linkRows, linkBase, from, to, CleanDaysBefore(linkRows, linkBase), parent,
                parent == null ? null : rows.GetValueOrDefault(parent.Id) ?? [], parent == null ? null : baseline.GetValueOrDefault(parent.Id) ?? [],
                sfp?.GetValueOrDefault(link.Id));
            ev.TroubleSince = TroubleSince(linkRows, linkBase);
            // A test that ran during an incident on the link is kept on the link but out of the norm comparison.
            if (speedTests != null && speedTests.TryGetValue(link.Id, out var tests))
                ev.SpeedTests.AddRange(tests.Select(t => t with
                {
                    DuringIncident = incidents.Any(i => !i.IsExplained && t.At >= i.Start.AddMinutes(-1) && t.At <= i.End.AddMinutes(1)),
                }));
            evidence[link.Id] = ev;
            var findings = LanHealthDetectors.Run(ev, incidents, text);
            var health = new LinkHealth
            {
                Link = link,
                Incidents = incidents,
                Findings = findings,
                Coverage = CoverageFromRows(linkRows),
                ErrorsUpstreamRx = ev.Upstream.RxErrors ?? 0,
                ErrorsDownstreamRx = ev.Downstream.RxErrors ?? 0,
                DiscardsUpstreamTx = ev.Upstream.TxDiscards ?? 0,
                DiscardsDownstreamTx = ev.Downstream.TxDiscards ?? 0,
                ProbeLossPct = ev.ProbeLossPct,
                Exclusion = ExclusionFor(link, exclusions, scoreFabricOnly),
                // Reaches back past the window to where the run of trouble began.
                Onset = findings.Where(f => f.Points > 0).Select(f => f.Onset).OfType<DateTime>().DefaultIfEmpty().Min() is var o && o != default
                    ? (ev.TroubleSince is DateTime ts && ts < o ? ts : o) : null,
                CleanDaysBefore = ev.HistoryDays,
                UpLinkDownCount = linkRows.LastOrDefault(r => r.UpLinkDownCount != null)?.UpLinkDownCount,
                DownLinkDownCount = linkRows.LastOrDefault(r => r.DownLinkDownCount != null)?.DownLinkDownCount,
                DownCarrierChanges = linkRows.LastOrDefault(r => r.DownCarrierChanges != null)?.DownCarrierChanges,
                PoeWatts = linkRows.LastOrDefault(r => r.PoeWattsMax != null)?.PoeWattsMax ?? link.UpstreamPort.PoePowerW,
            };
            health.Score = health.Exclusion == ExclusionLevel.ExcludeFromScore ? 100 : LanHealthScorer.LinkScore(findings);
            report.Links.Add(health);
        }

        if (site != null)
        {
            var siteFindings = LanSiteDetectors.Run(site(report.Links, evidence));
            foreach (var f in siteFindings)
            {
                var owner = report.Links.FirstOrDefault(l => l.Link.Id == f.LinkId);
                if (owner != null)
                {
                    owner.Findings.Add(f);
                    if (f.Points > 0 && owner.Exclusion != ExclusionLevel.ExcludeFromScore) owner.Score = LanHealthScorer.LinkScore(owner.Findings);
                }
                else report.Findings.Add(f);
            }
        }

        foreach (var l in report.Links) l.Status = LanHealthScorer.Status(l);
        report.Findings.InsertRange(0, report.Links.Where(l => l.Exclusion != ExclusionLevel.ExcludeFromScore).SelectMany(l => l.Findings));
        report.Findings.Sort((a, b) => b.Points != a.Points ? b.Points.CompareTo(a.Points) : b.Severity.CompareTo(a.Severity));
        report.Score = LanHealthScorer.SiteScore(report.Links);
        ScoreImpacts(report);
        // Biggest cost to the LAN Health Score first: the order matches the numbers shown.
        report.Findings.Sort((a, b) => b.ScoreImpact != a.ScoreImpact ? b.ScoreImpact.CompareTo(a.ScoreImpact)
            : b.Points != a.Points ? b.Points.CompareTo(a.Points) : b.Severity.CompareTo(a.Severity));
        report.FabricLinks = report.Links.Count(l => l.Link.IsFabric);
        report.AccessPorts = report.Links.Count(l => !l.Link.IsFabric);
        report.Incidents = (int)report.Links.Sum(l => l.Incidents.Where(i => !i.IsExplained && i.Kind != IncidentKind.DiscardBurst).Sum(i => i.Count));
        report.Explained = (int)report.Links.Sum(l => l.Incidents.Where(i => i.IsExplained).Sum(i => i.Count));
        return report;
    }

    /// <summary>Coverage summed over the window's hours.</summary>
    public static List<SourceCoverage> CoverageFromRows(IReadOnlyList<LanHealthLinkHour> rows)
    {
        var bySource = new Dictionary<LinkSource, (int Caught, int Total, bool Observing, DateTime? Since)>();
        foreach (var r in rows.OrderBy(r => r.HourStart))
            foreach (var c in LanHealthIncidentJson.DeserializeCoverage(r.CoverageJson))
            {
                var s = (LinkSource)c.Source;
                var cur = bySource.GetValueOrDefault(s);
                bySource[s] = (cur.Caught + c.Caught, cur.Total + c.Total, cur.Observing || c.Observing,
                    cur.Since ?? (c.Observing ? r.HourStart : null));
            }
        return bySource.Select(kv => new SourceCoverage(kv.Key, kv.Value.Caught, kv.Value.Total, kv.Value.Observing, kv.Value.Since)).ToList();
    }

    /// <summary>Seconds a drop costs the clients behind it: an AP's clients reassociate and renew; a wired client relinks.</summary>
    internal const double ApDropDisruptionSeconds = 60, WiredDropDisruptionSeconds = 10;

    /// <summary>
    /// The hourly cost of an unstable link while its Link Flapping or link error finding is open, by severity, before
    /// the client share. It is a condition, like Wi-Fi satisfaction: the link is unreliable between drops too.
    /// </summary>
    internal static double ConditionCost(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 0.3,
        FindingSeverity.Warning => 0.15,
        _ => 0.05,
    };

    /// <summary>Findings that make a link unstable for every hour they are open.</summary>
    internal static bool IsUnstable(LinkFinding f) => f.Detector is Detectors.LinkDrops or Detectors.StableLinkErrors or Detectors.ErrorAsymmetry;

    /// <summary>
    /// The hour's own LAN impairment for Site Health: the share of user-time the LAN cost. Each link
    /// weighs by its clients' share of the site. A drop costs its seconds down or its clients' reconnect
    /// time, whichever is longer; a probe loss burst five seconds; a steady error rate ramps as Site
    /// Health's port error factor did. A link with an open unstable finding (<paramref name="open"/>,
    /// the trailing window's at this hour) also costs its <see cref="ConditionCost"/>.
    /// </summary>
    public static (double Impairment, string? Worst) HourImpairment(IReadOnlyList<LanHealthLinkHour> hour, IReadOnlyList<LinkRecord> links,
        IReadOnlyList<LanHealthExclusion> exclusions, bool scoreFabricOnly, IReadOnlyList<LinkFinding>? open = null)
    {
        var rowsById = hour.GroupBy(r => r.LinkId).ToDictionary(g => g.Key, g => g.First());
        var condition = (open ?? []).Where(IsUnstable).GroupBy(f => f.LinkId)
            .ToDictionary(g => g.Key, g => g.Max(f => ConditionCost(f.Severity)));
        var byId = links.ToDictionary(l => l.Id);
        var total = Math.Max(1, links.Where(l => !l.IsFabric).Sum(l => l.ClientsBehind) + links.Where(l => l.IsFabric && l.Upstream.IsGateway).Sum(l => l.ClientsBehind));
        double sum = 0;
        (string Name, double Part)? worst = null;
        foreach (var id in rowsById.Keys.Union(condition.Keys))
        {
            if (!byId.TryGetValue(id, out var link)) continue;
            if (ExclusionFor(link, exclusions, scoreFabricOnly) == ExclusionLevel.ExcludeFromScore) continue;
            var share = link.IsFabric ? Math.Max(1.0 / Math.Max(4, links.Count(l => l.IsFabric) + 1), (double)link.ClientsBehind / total) : 1.0 / total;
            var r = rowsById.GetValueOrDefault(id);
            var disruption = (r?.Drops ?? 0) * (link.Downstream.IsAccessPoint ? ApDropDisruptionSeconds : WiredDropDisruptionSeconds);
            var down = r == null ? 0 : Math.Min(1, (Math.Max(r.DownSeconds, disruption) + r.LossBursts * 5) / 3600);
            var packets = (r?.UpRxPackets ?? 0) + (r?.DownRxPackets ?? 0);
            var errors = r?.ErrorBursts ?? 0;
            var errorPart = packets >= 10_000 ? ChangeControl.SiteHealth.SiteHealthScorer.Ramp(errors / packets, 0.0001, 0.01) : 0;
            var part = Math.Min(1, share) * Math.Clamp(down + errorPart + condition.GetValueOrDefault(id), 0, 1);
            if (part <= 0) continue;
            sum += part;
            if (worst == null || part > worst.Value.Part) worst = (link.Name, part);
        }
        return (Math.Clamp(sum, 0, 1), worst is { } w ? w.Name : null);
    }
}

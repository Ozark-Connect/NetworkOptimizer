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
        {
            var hit = x.Scope switch
            {
                "port" => x.Key == link.Id,
                "device" => string.Equals(x.Key, link.Upstream.DeviceMac, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Key, link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase),
                "profile" => !string.IsNullOrEmpty(link.UpstreamPort.PortProfileId) && x.Key == link.UpstreamPort.PortProfileId,
                "access-all" => link.Kind == LinkKind.Access,
                _ => false,
            };
            if (hit) Take(x.Level);
        }
        if (scoreFabricOnly && link.Kind == LinkKind.Access) Take((int)ExclusionLevel.ExcludeFromScore);
        // Fabric links can be muted but never excluded: a fabric fault hits everyone behind it.
        if (link.IsFabric && level == ExclusionLevel.ExcludeFromScore) level = ExclusionLevel.MuteAlerts;
        return level;
    }

    /// <summary>One link's evidence over a window, from its stored hours.</summary>
    public static LinkEvidence EvidenceFromRows(LinkRecord link, IReadOnlyList<LanHealthLinkHour> rows, IReadOnlyList<LanHealthLinkHour> baseline,
        DateTime from, DateTime to, int? historyDays)
    {
        double? Sum(Func<LanHealthLinkHour, double?> f) => rows.Any(r => f(r) != null) ? rows.Sum(r => f(r) ?? 0) : null;
        var upBlind = rows.Count > 0 && rows.All(r => r.UpErrorsBlind);
        var speeds = rows.Where(r => r.SpeedMinMbps is > 0)
            .SelectMany(r => new[] { new SpeedReading(r.HourStart, r.SpeedMinMbps!.Value, null, LinkSource.PortTableState),
                new SpeedReading(r.HourStart.AddMinutes(59), r.SpeedMaxMbps ?? r.SpeedMinMbps!.Value, null, LinkSource.PortTableState) })
            .ToList();
        var usual = baseline.Concat(rows).Select(r => r.SpeedMaxMbps).OfType<int>().DefaultIfEmpty().Max();
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
            DownstreamReboots = rows.Where(r => r.Reboots > 0).Select(r => r.HourStart.AddMinutes(30)).ToList(),
            PoeDraw = rows.Where(r => r.PoeWattsMin != null).SelectMany(r => new[] { (r.HourStart, r.PoeWattsMin!.Value), (r.HourStart.AddMinutes(30), r.PoeWattsMax ?? r.PoeWattsMin!.Value) }).ToList(),
            ProbeLossPct = rows.Any(r => r.ProbeLossPct != null) ? rows.Where(r => r.ProbeLossPct != null).Average(r => r.ProbeLossPct!.Value) : null,
            HistoryDays = historyDays,
        };
    }

    private static LinkHourSample Sample(LanHealthLinkHour r) => new(r.HourStart, r.MaxInBps, r.MaxOutBps, r.MeanInBps, r.MeanOutBps,
        r.BroadcastPps, r.MulticastPps, r.PacketsPps, (r.UpTxDiscards ?? 0) + (r.DownTxDiscards ?? 0), (r.UpRxErrors ?? 0) + (r.DownRxErrors ?? 0));

    /// <summary>
    /// Clean days of history before the first unexplained drop or loss burst in the window: how far
    /// back the stored hours go with none, so a finding can say "with none in the N days before".
    /// </summary>
    public static int? CleanDaysBefore(IReadOnlyList<LanHealthLinkHour> rows, IReadOnlyList<LanHealthLinkHour> baseline)
    {
        var first = rows.Where(r => r.Drops + r.Relinks + r.LossBursts > 0).OrderBy(r => r.HourStart).FirstOrDefault();
        if (first == null || baseline.Count == 0) return null;
        var before = baseline.Where(r => r.HourStart < first.HourStart).OrderByDescending(r => r.HourStart).ToList();
        if (before.Count == 0) return null;
        var dirty = before.FirstOrDefault(r => r.Drops + r.Relinks + r.LossBursts > 0);
        var cleanFrom = dirty?.HourStart ?? before[^1].HourStart;
        var days = (int)Math.Floor((first.HourStart - cleanFrom).TotalDays);
        return days >= 1 ? days : null;
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
        DateTime from, DateTime to)
    {
        var report = new LanHealthReport { From = from, To = to, Ready = rows.Count > 0 };
        var evidence = new Dictionary<string, LinkEvidence>();
        foreach (var link in links)
        {
            var linkRows = rows.GetValueOrDefault(link.Id) ?? [];
            var linkBase = baseline.GetValueOrDefault(link.Id) ?? [];
            var incidents = linkRows.SelectMany(r => LanHealthIncidentJson.Deserialize(r.IncidentsJson)).OrderBy(i => i.Start).ToList();
            var ev = EvidenceFromRows(link, linkRows, linkBase, from, to, CleanDaysBefore(linkRows, linkBase));
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
                Onset = findings.Where(f => f.Points > 0).Select(f => f.Onset).OfType<DateTime>().DefaultIfEmpty().Min() is var o && o != default ? o : null,
                CleanDaysBefore = ev.HistoryDays,
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

    /// <summary>
    /// The hour's own LAN impairment for Site Health: the share of user-time the LAN cost. Each link
    /// weighs by its clients' share of the site; a drop costs its seconds down, a probe loss burst
    /// five seconds, and a steady error rate ramps as Site Health's port error factor did.
    /// </summary>
    public static (double Impairment, string? Worst) HourImpairment(IReadOnlyList<LanHealthLinkHour> hour, IReadOnlyList<LinkRecord> links,
        IReadOnlyList<LanHealthExclusion> exclusions, bool scoreFabricOnly)
    {
        var byId = links.ToDictionary(l => l.Id);
        var total = Math.Max(1, links.Where(l => !l.IsFabric).Sum(l => l.ClientsBehind) + links.Where(l => l.IsFabric && l.Upstream.IsGateway).Sum(l => l.ClientsBehind));
        double sum = 0;
        (string Name, double Part)? worst = null;
        foreach (var r in hour)
        {
            if (!byId.TryGetValue(r.LinkId, out var link)) continue;
            if (ExclusionFor(link, exclusions, scoreFabricOnly) == ExclusionLevel.ExcludeFromScore) continue;
            var share = link.IsFabric ? Math.Max(1.0 / Math.Max(4, links.Count(l => l.IsFabric) + 1), (double)link.ClientsBehind / total) : 1.0 / total;
            var down = Math.Min(1, (r.DownSeconds + r.LossBursts * 5) / 3600);
            var packets = (r.UpRxPackets ?? 0) + (r.DownRxPackets ?? 0);
            var errors = r.ErrorBursts;
            var errorPart = packets >= 10_000 ? ChangeControl.SiteHealth.SiteHealthScorer.Ramp(errors / packets, 0.0001, 0.01) : 0;
            var part = Math.Min(1, share) * Math.Clamp(down + errorPart, 0, 1);
            if (part <= 0) continue;
            sum += part;
            if (worst == null || part > worst.Value.Part) worst = (link.Name, part);
        }
        return (Math.Clamp(sum, 0, 1), worst is { } w ? w.Name : null);
    }
}

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Turns findings into scores. A link starts at 100 and each scored finding takes its points as an
/// independent impairment (two 20-point findings leave 64, not 60). The site score is the
/// client-weighted roll-up: a fabric link weighs by everything behind it, an access port by its one
/// client, and an excluded link not at all. Findings at 0 pts (load, config, capacity) never move it.
/// </summary>
public static class LanHealthScorer
{
    /// <summary>Score at or above this reads as healthy (the map shows nothing for it).</summary>
    public const double HealthyScore = 90;

    /// <summary>The weight of a drop in the link's quietest hour, as ISP Health's usage weighting floor.</summary>
    public const double UsageWeightFloor = 0.5;

    /// <summary>
    /// The link's hour-of-day traffic profile (mean of both directions per local hour) over its baseline and window,
    /// for <see cref="IspHealth.UsageWeighting"/>; null under a day of hours, which leaves every drop at full weight.
    /// </summary>
    internal static double[]? UsageProfile(IEnumerable<LinkHourSample> hours, TimeZoneInfo zone)
    {
        var list = hours.Where(h => h.MeanInBps != null || h.MeanOutBps != null).ToList();
        if (list.Count < 24) return null;
        var profile = new double[24];
        foreach (var g in list.GroupBy(h => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(h.Hour, DateTimeKind.Utc), zone).Hour))
            profile[g.Key] = g.Average(h => (h.MeanInBps ?? 0) + (h.MeanOutBps ?? 0));
        return profile;
    }

    /// <summary>A drop's weight by the link's own usage at that local hour: 1 in its busiest hour, down to the floor when idle.</summary>
    internal static double UsageWeight(double[]? profile, DateTime utc, TimeZoneInfo zone) =>
        IspHealth.UsageWeighting.Weight(profile, [TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).Hour], UsageWeightFloor);

    /// <summary>The link's score from its findings.</summary>
    public static double LinkScore(IEnumerable<LinkFinding> findings)
    {
        var remaining = 1.0;
        foreach (var f in findings.Where(f => f.Points > 0))
            remaining *= 1 - Math.Clamp(f.Points, 0, 100) / 100;
        return Math.Round(100 * remaining, 1);
    }

    /// <summary>The status pill (lanh-21): the worst scored finding's, else Busy from load, else Healthy.</summary>
    public static LinkStatus Status(LinkHealth link)
    {
        if (link.Exclusion == ExclusionLevel.ExcludeFromScore) return LinkStatus.Excluded;
        var status = link.Findings.Where(f => f.Status != null && f.Points > 0).OrderByDescending(f => f.Points).Select(f => f.Status).FirstOrDefault()
            ?? link.Findings.Where(f => f.Status == LinkStatus.Busy).Select(f => f.Status).FirstOrDefault()
            ?? LinkStatus.Healthy;
        if (link.Exclusion == ExclusionLevel.MuteAlerts && status == LinkStatus.Healthy) return LinkStatus.Muted;
        return status;
    }

    /// <summary>
    /// The site score: 100 minus each link's lost points weighted by its share of the site's clients.
    /// A fabric link counts its subtree's clients (at least one device's worth); an access port one client.
    /// Fabric links cannot be excluded from the site score: a fabric fault hits everyone behind it.
    /// </summary>
    public static double? SiteScore(IReadOnlyList<LinkHealth> links)
    {
        var scored = links.Where(l => l.Link.IsFabric || l.Exclusion != ExclusionLevel.ExcludeFromScore).ToList();
        if (scored.Count == 0) return null;
        var totalClients = Math.Max(1, scored.Where(l => !l.Link.IsFabric).Sum(l => l.Link.ClientsBehind)
            + scored.Where(l => l.Link.IsFabric && l.Link.Upstream.IsGateway).Sum(l => l.Link.ClientsBehind));
        var floorShare = 1.0 / Math.Max(4, scored.Count(l => l.Link.IsFabric) + 1);
        var lost = 0.0;
        foreach (var l in scored)
        {
            var share = l.Link.IsFabric
                ? Math.Max(floorShare, (double)l.Link.ClientsBehind / totalClients)
                : 1.0 / totalClients;
            lost += (100 - l.Score) * Math.Min(1, share);
        }
        return Math.Round(Math.Clamp(100 - lost, 0, 100), 1);
    }

    /// <summary>The site's LAN impairment for Site Health (0 none, 1 everyone), from the site score.</summary>
    public static double Impairment(double siteScore) => Math.Clamp((100 - siteScore) / 100, 0, 1);
}

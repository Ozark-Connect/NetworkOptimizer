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
        var records = scored.Select(l => l.Link).ToList();
        var totalClients = TotalClients(records);
        var floorShare = FloorShare(records);
        var lost = 0.0;
        foreach (var l in scored)
            lost += (100 - l.Score) * Share(l.Link, totalClients, floorShare);
        return Math.Round(Math.Clamp(100 - lost, 0, 100), 1);
    }

    /// <summary>
    /// The site's clients, each once: every gateway fabric subtree's, plus access ports outside those subtrees (the
    /// gateway's own ports). A switch's access ports are already inside its uplink's subtree.
    /// </summary>
    internal static int TotalClients(IReadOnlyList<LinkRecord> links)
    {
        var children = links.Where(l => l.IsFabric).GroupBy(l => l.Upstream.DeviceMac, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Downstream.DeviceMac).ToList(), StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(links.Where(l => l.IsFabric && l.Upstream.IsGateway).Select(l => l.Downstream.DeviceMac));
        while (stack.Count > 0)
        {
            var m = stack.Pop();
            if (!covered.Add(m)) continue;
            foreach (var c in children.GetValueOrDefault(m) ?? []) stack.Push(c);
        }
        return Math.Max(1, links.Where(l => l.IsFabric && l.Upstream.IsGateway).Sum(l => l.ClientsBehind)
            + links.Where(l => !l.IsFabric && !covered.Contains(l.Upstream.DeviceMac)).Sum(l => l.ClientsBehind));
    }

    /// <summary>The least a fabric link that feeds other devices weighs: a fault there reaches everything behind it.</summary>
    internal static double FloorShare(IReadOnlyList<LinkRecord> links) => 1.0 / Math.Max(4, links.Count(l => l.IsFabric) + 1);

    /// <summary>
    /// A link's share of the site: an access port its one client; a fabric link to a leaf (one device, not a switch)
    /// the clients on that device; any other fabric link its subtree's clients, at least <paramref name="floorShare"/>.
    /// </summary>
    internal static double Share(LinkRecord link, int totalClients, double floorShare)
    {
        if (!link.IsFabric) return Math.Min(1, 1.0 / totalClients);
        var clients = (double)link.ClientsBehind / totalClients;
        var leaf = link.DevicesBehind <= 1 && !link.Downstream.IsSwitch;
        return Math.Min(1, leaf ? Math.Max(1.0, link.ClientsBehind) / totalClients : Math.Max(floorShare, clients));
    }

    /// <summary>The site's LAN impairment for Site Health (0 none, 1 everyone), from the site score.</summary>
    public static double Impairment(double siteScore) => Math.Clamp((100 - siteScore) / 100, 0, 1);
}

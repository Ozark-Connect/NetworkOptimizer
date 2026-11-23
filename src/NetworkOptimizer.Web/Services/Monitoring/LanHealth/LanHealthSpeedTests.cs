using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Puts LAN speed tests on the links they crossed and judges each against the norm for its path
/// shape (link speeds on the path, hop count, direction, streams, routed or not). Retransmits are
/// normal for AP-terminated tests and are only ever compared within a shape. A test that ran during
/// an incident on the link is left out of the norm and kept on the incident's link.
/// </summary>
public static class LanHealthSpeedTests
{
    /// <summary>One test reduced to what attribution needs.</summary>
    public sealed record Test(DateTime At, double DownMbps, double UpMbps, int Retransmits, string Shape, string Kind, bool Routed, IReadOnlyList<string> HopMacs);

    /// <summary>Tests from stored results; failed tests and tests without a path are dropped.</summary>
    public static List<Test> FromResults(IEnumerable<Iperf3Result> results) =>
        results.Where(r => r.Success && r.PathAnalysis?.Path?.Hops is { Count: > 0 })
            .Select(r =>
            {
                var path = r.PathAnalysis!.Path!;
                var macs = path.Hops.OrderBy(h => h.Order).Select(h => LanLinkBuilder.Mac(h.DeviceMac)).Where(m => m.Length > 0).ToList();
                var shape = $"{path.TheoreticalMaxMbps}|{path.Hops.Count}|{r.Direction}|{r.ParallelStreams}|{path.RequiresRouting}";
                return new Test(DateTime.SpecifyKind(r.TestTime, DateTimeKind.Utc), r.DownloadBitsPerSecond / 1e6, r.UploadBitsPerSecond / 1e6,
                    r.DownloadRetransmits + r.UploadRetransmits, shape, r.Direction.ToString(), path.RequiresRouting, macs);
            })
            .ToList();

    /// <summary>The median result per shape, from at least three tests.</summary>
    public static Dictionary<string, (double Down, double Up)> Norms(IReadOnlyList<Test> tests) =>
        tests.GroupBy(t => t.Shape).Where(g => g.Count() >= 3)
            .ToDictionary(g => g.Key, g => (ChangeControl.SiteHealth.SiteHealthJob.Median(g.Select(t => t.DownMbps)),
                ChangeControl.SiteHealth.SiteHealthJob.Median(g.Select(t => t.UpMbps))));

    /// <summary>The fabric links a test crossed: each consecutive pair of devices on its path that is a link.</summary>
    public static IEnumerable<LinkRecord> Crossed(Test t, IReadOnlyList<LinkRecord> links)
    {
        for (var i = 0; i + 1 < t.HopMacs.Count; i++)
        {
            var a = t.HopMacs[i];
            var b = t.HopMacs[i + 1];
            var link = links.FirstOrDefault(l => l.IsFabric
                && (l.Upstream.DeviceMac == a && l.Downstream.DeviceMac == b || l.Upstream.DeviceMac == b && l.Downstream.DeviceMac == a));
            if (link != null) yield return link;
        }
    }

    /// <summary>Per link, the tests in the window that crossed it, each with its shape's norm.</summary>
    public static Dictionary<string, List<LinkSpeedTest>> Attribute(IReadOnlyList<Test> tests, IReadOnlyList<LinkRecord> links, DateTime from, DateTime to)
    {
        var norms = Norms(tests);
        var result = new Dictionary<string, List<LinkSpeedTest>>();
        foreach (var t in tests.Where(t => t.At >= from && t.At < to))
        {
            var norm = norms.TryGetValue(t.Shape, out var n) ? n : ((double, double)?)null;
            foreach (var link in Crossed(t, links))
            {
                if (!result.TryGetValue(link.Id, out var list)) result[link.Id] = list = [];
                list.Add(new LinkSpeedTest(t.At, t.DownMbps, t.UpMbps, t.Retransmits, t.Shape, t.Kind, norm?.Item1, norm?.Item2, false));
            }
        }
        return result;
    }

    /// <summary>Gateway-routed tests in the window against their norms, for detector 32.</summary>
    public static (int Tests, double RatioToNorm)? GatewayForwarding(IReadOnlyList<Test> tests, DateTime from, DateTime to)
    {
        var norms = Norms(tests.Where(t => t.At < from).ToList());
        var recent = tests.Where(t => t.Routed && t.At >= from && t.At < to && norms.ContainsKey(t.Shape)).ToList();
        if (recent.Count == 0) return null;
        var ratio = recent.Average(t =>
        {
            var n = norms[t.Shape];
            return Math.Max(n.Down > 0 ? t.DownMbps / n.Down : 0, n.Up > 0 ? t.UpMbps / n.Up : 0);
        });
        return (recent.Count, ratio);
    }

    /// <summary>
    /// A suggestion for each fabric link no test has crossed in 30 days: the cheapest test that would,
    /// a LAN Speed Test to the device when it can run iperf3, else a client test from below it. Never runs one.
    /// </summary>
    public static IEnumerable<LinkFinding> Suggestions(IReadOnlyList<Test> tests, IReadOnlyList<LinkRecord> links, DateTime now,
        Func<string, bool> canRunIperf3)
    {
        var tested = tests.Where(t => t.At >= now.AddDays(-30)).SelectMany(t => Crossed(t, links)).Select(l => l.Id).ToHashSet();
        foreach (var l in links.Where(l => l.Kind == LinkKind.FabricWired && !tested.Contains(l.Id)))
        {
            var body = canRunIperf3(l.Downstream.DeviceMac)
                ? $"No speed test has crossed this link in 30 days. A LAN Speed Test to {l.Downstream.DeviceName} measures it."
                : $"No speed test has crossed this link in 30 days. A Client Speed Test from a wired client below {l.Downstream.DeviceName} measures it.";
            yield return new LinkFinding
            {
                Detector = Detectors.CapacityTrend,
                LinkId = l.Id,
                Severity = FindingSeverity.Info,
                Class = "Untested Link",
                Title = $"Untested Link: {l.Name}",
                Body = body,
                Chips = ["Speed tests"],
            };
        }
    }

    /// <summary>
    /// A wired client on another VLAN whose path crosses the trunks to the gateway and back, at a speed
    /// that can fill them: one Client Speed Test from it measures inter-VLAN routing and every trunk on the way.
    /// </summary>
    public static LinkFinding? CrossVlanCandidate(IReadOnlyList<Test> tests, IReadOnlyList<LinkRecord> links,
        IReadOnlyList<(string Mac, string Name, string? Network, int? SpeedMbps, string? SwitchMac)> wiredClients, string? serverNetwork, DateTime now)
    {
        if (tests.Any(t => t.Routed && t.At >= now.AddDays(-30))) return null;
        var trunkSpeed = links.Where(l => l.Kind == LinkKind.FabricWired && l.Upstream.IsGateway).Select(l => l.SpeedMbps).OfType<int>().DefaultIfEmpty(0).Max();
        var candidate = wiredClients.Where(c => !string.IsNullOrEmpty(c.Network) && c.Network != serverNetwork
                && c.SpeedMbps is int s && s >= trunkSpeed && trunkSpeed > 0 && !string.IsNullOrEmpty(c.SwitchMac)
                && links.Any(l => l.Kind == LinkKind.FabricWired && l.Downstream.DeviceMac == LanLinkBuilder.Mac(c.SwitchMac)))
            .OrderByDescending(c => c.SpeedMbps).FirstOrDefault();
        if (candidate.Mac == null) return null;
        return new LinkFinding
        {
            Detector = Detectors.HairpinLoad,
            Severity = FindingSeverity.Info,
            Class = "Cross-VLAN Test",
            Title = $"Cross-VLAN Test: {candidate.Name}",
            Body = $"A Client Speed Test from {candidate.Name} on {candidate.Network} crosses the trunks to the gateway and back at {DisplayFormatters.FormatLinkSpeed(candidate.SpeedMbps!.Value)}: one run measures the gateway's routing between VLANs and every trunk on the way.",
            Chips = ["Speed tests"],
        };
    }
}

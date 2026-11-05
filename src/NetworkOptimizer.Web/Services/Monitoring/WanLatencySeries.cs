using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// The WAN live chart's RTT and loss history: the ISP access floor (<see cref="AccessFloor"/>, as
/// ISP Health reads the access layer) and the Transit mean, then the mean of the two, so ISP and
/// Transit carry half the line each. The live tick computes the same thing from current readings
/// (<see cref="MonitoringLiveStats.GetMeanIspTransitLiveAsync"/>), so the chart does not jump when
/// its buffer moves from history to live samples. The WAN globe's loss reads it too.
/// </summary>
public static class WanLatencySeries
{
    /// <summary>The combined series over [<paramref name="from"/>, <paramref name="to"/>].</summary>
    /// <param name="targetIds">The ISP and Transit targets of the WAN being charted.</param>
    /// <param name="wanScope">Which WAN's points count; see <see cref="MonitoringInfluxClient.LatencyWanScope"/>.</param>
    public static async Task<IReadOnlyList<MonitoringInfluxClient.LatencyPoint>> QueryAsync(
        MonitoringInfluxClient influx, DateTime from, DateTime to, IReadOnlyList<string> targetIds,
        MonitoringInfluxClient.LatencyWanScope? wanScope = null, CancellationToken ct = default)
    {
        var window = TimeSpan.FromSeconds(Math.Max(10, (int)((to - from).TotalSeconds / 150)));
        // A lead-in so every target has probed once before the first visible window; without it the
        // oldest windows combine a partial set of targets and the chart's left edge skews.
        var warmup = TimeSpan.FromSeconds(60) + window;
        var rows = await influx.QueryIspTransitLatencyByTargetAsync(from - warmup, to, targetIds, window, ct, wanScope);
        return Combine(rows, from);
    }

    /// <summary>
    /// Per-target window means combined into the chart's series, from <paramref name="from"/> on. A
    /// target carries its last reading through windows it missed, so a slow prober does not make the
    /// line saw up and down as it drops in and out of the mean.
    /// </summary>
    internal static List<MonitoringInfluxClient.LatencyPoint> Combine(
        IReadOnlyList<MonitoringInfluxClient.TargetLatencyMean> rows, DateTime from)
    {
        var times = rows.Select(r => r.Time).Distinct().Order().ToList();
        if (times.Count == 0) return [];

        var isp = new List<IReadOnlyList<LatencySample>>();
        var transit = new List<IReadOnlyList<LatencySample>>();
        foreach (var target in rows.GroupBy(r => r.TargetId))
        {
            var filled = CarryForward(target, times);
            if (filled.Count == 0) continue;
            (target.First().TargetType == "accessisp" ? isp : transit).Add(filled);
        }

        var floor = new Dictionary<DateTime, LatencySample>();
        var floorLoss = new Dictionary<DateTime, double>();
        var hops = isp.Where(s => s.Any(p => p.RttAvgMs != null)).ToList();
        if (hops.Count > 0)
        {
            var lowest = hops.MinBy(s => SeriesStats.Median(
                s.Where(p => p.RttAvgMs != null).Select(p => p.RttAvgMs!.Value).ToList()) ?? double.MaxValue)!;
            foreach (var s in AccessFloor.Build(hops, lowest)) floor[s.Time] = s;
            // Before the lowest-median hop's first reading (a hop added mid-range), the hops' mean.
            foreach (var (time, loss) in AccessFloor.LossSeries(hops, lowest)) floorLoss[time] = loss;
        }
        // A WAN whose ISP hops never answered still reports their loss: a blackhole is what that looks like.
        var ispLossOnly = hops.Count == 0 && isp.Count > 0
            ? isp.SelectMany(s => s).Where(p => p.LossPercent != null).GroupBy(p => p.Time)
                .ToDictionary(g => g.Key, g => g.Average(p => p.LossPercent!.Value))
            : [];

        var transitByTime = transit.SelectMany(s => s).GroupBy(p => p.Time).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<MonitoringInfluxClient.LatencyPoint>();
        foreach (var time in times.Where(t => t >= from))
        {
            floor.TryGetValue(time, out var f);
            transitByTime.TryGetValue(time, out var tr);
            var transitRtt = tr?.Where(p => p.RttAvgMs != null).Select(p => p.RttAvgMs!.Value).DefaultIfEmpty(double.NaN).Average();
            var transitLoss = tr?.Where(p => p.LossPercent != null).Select(p => p.LossPercent!.Value).DefaultIfEmpty(double.NaN).Average();
            var ispLoss = floorLoss.TryGetValue(time, out var fl) ? fl
                : ispLossOnly.TryGetValue(time, out var l) ? l : (double?)null;

            var rtt = MeanOf(f?.RttAvgMs, Finite(transitRtt));
            var loss = MeanOf(ispLoss, Finite(transitLoss));
            if (rtt == null && loss == null) continue;
            result.Add(new MonitoringInfluxClient.LatencyPoint { Time = time, RttAvgMs = rtt, LossPercent = loss });
        }
        return result;
    }

    /// <summary>One target's samples on every window from its first reading, each field carrying forward.</summary>
    private static List<LatencySample> CarryForward(IEnumerable<MonitoringInfluxClient.TargetLatencyMean> rows, IReadOnlyList<DateTime> times)
    {
        var byTime = rows.GroupBy(r => r.Time).ToDictionary(g => g.Key, g => g.Last());
        var result = new List<LatencySample>();
        double? rtt = null, loss = null;
        foreach (var time in times)
        {
            if (byTime.TryGetValue(time, out var row))
            {
                rtt = row.RttAvgMs ?? rtt;
                loss = row.LossPercent ?? loss;
            }
            if (rtt == null && loss == null) continue;
            result.Add(new LatencySample(time, rtt, null, null, loss));
        }
        return result;
    }

    private static double? Finite(double? v) => v is double d && double.IsFinite(d) ? d : null;

    // The mean of the halves present: ISP and Transit weigh the same, and a missing half is skipped, not zero.
    private static double? MeanOf(double? a, double? b) =>
        a != null && b != null ? (a.Value + b.Value) / 2 : a ?? b;
}

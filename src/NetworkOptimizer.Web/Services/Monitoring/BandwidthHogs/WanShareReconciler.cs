namespace NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;

/// <summary>
/// Splits a WAN's measured rate across the clients that could have produced it. There is no
/// per-client WAN counter at second resolution, so this is a reconciliation, per direction:
/// <list type="number">
/// <item>The clients' rates add up to the WAN rate within <see cref="Threshold"/>: the WAN explains
/// the whole load, nothing is local, every client's WAN rate is its measured rate.</item>
/// <item>They exceed it: some traffic never left the site. Each client is bounded by its measured
/// rate and by what its uplink chain carried. The console's per-client WAN rate, where fresh,
/// sets a floor inside that bound and a soft cap of <see cref="ConsoleCapFactor"/> times itself;
/// the WAN rate is water-filled by recent DPI bytes under the soft caps first, and whatever they
/// cannot place is filled again under the hard bounds alone. The console rate lags by tens of
/// seconds, so it steers the split but never decides it. A client with no recent DPI bytes gets
/// nothing beyond its floor; when nobody has any (DPI unavailable) the measured rates weight it
/// instead.</item>
/// </list>
/// </summary>
public static class WanShareReconciler
{
    /// <summary>How far past the WAN rate the clients' total may run before some of it is called local.</summary>
    public const double Threshold = 0.15;

    /// <summary>
    /// The console's WAN rate times this is a client's soft cap. Generous because a burst that
    /// started after the console last looked is not in its figure yet.
    /// </summary>
    public const double ConsoleCapFactor = 2.0;

    /// <summary>One client's measured rate, its DPI WAN bytes over the recent window, the least its
    /// uplink chain carried (null when no hop had a rate), and the console's WAN rate for it (null
    /// when there is no fresh one).</summary>
    public readonly record struct Load(double RateBps, double DpiBytes, double? ChainCapBps, double? ConsoleWanBps = null);

    /// <summary>Per-client WAN rates in input order, and whether they are an estimate (case 2).</summary>
    public readonly record struct Split(double[] WanBps, bool Estimated);

    public static Split Allocate(double wanRateBps, IReadOnlyList<Load> loads, double threshold = Threshold)
    {
        var n = loads.Count;
        var wan = new double[n];
        if (n == 0 || wanRateBps <= 0) return new Split(wan, false);

        var caps = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var rate = Math.Max(0, loads[i].RateBps);
            caps[i] = loads[i].ChainCapBps is { } chain ? Math.Min(rate, Math.Max(0, chain)) : rate;
            sum += rate;
        }

        if (sum <= wanRateBps * (1 + threshold))
        {
            Array.Copy(caps, wan, n);
            return new Split(wan, false);
        }

        var weights = new double[n];
        var anyDpi = false;
        for (var i = 0; i < n; i++)
        {
            weights[i] = Math.Max(0, loads[i].DpiBytes);
            anyDpi |= weights[i] > 0;
        }
        if (!anyDpi)
            for (var i = 0; i < n; i++) weights[i] = caps[i];

        // Console floors first: what the console saw a client move on the WAN, it moved. Scaled
        // down together when they claim more than the WAN carried, since they lag it.
        double floors = 0;
        for (var i = 0; i < n; i++)
        {
            if (loads[i].ConsoleWanBps is not { } console) continue;
            wan[i] = Math.Min(Math.Max(0, console), caps[i]);
            floors += wan[i];
        }
        if (floors > wanRateBps)
        {
            var scale = wanRateBps / floors;
            for (var i = 0; i < n; i++) wan[i] *= scale;
            return new Split(wan, true);
        }

        var remaining = wanRateBps - floors;
        var soft = new double[n];
        for (var i = 0; i < n; i++)
            soft[i] = loads[i].ConsoleWanBps is { } console ? Math.Min(caps[i], Math.Max(0, console) * ConsoleCapFactor) : caps[i];

        remaining = WaterFill(remaining, weights, soft, wan);
        WaterFill(remaining, weights, caps, wan);
        return new Split(wan, true);
    }

    /// <summary>
    /// Shares <paramref name="remaining"/> across the clients by weight, each taking at most
    /// <paramref name="limit"/> less what it already holds; a client that overflows takes its limit
    /// and the rest is re-shared. Returns what could not be placed.
    /// </summary>
    private static double WaterFill(double remaining, double[] weights, double[] limit, double[] wan)
    {
        var active = new List<int>();
        for (var i = 0; i < wan.Length; i++)
            if (weights[i] > 0 && limit[i] - wan[i] > 0) active.Add(i);

        while (active.Count > 0 && remaining > 0)
        {
            double totalWeight = 0;
            foreach (var i in active) totalWeight += weights[i];
            if (totalWeight <= 0) break;

            var saturated = new List<int>();
            foreach (var i in active)
            {
                var share = remaining * weights[i] / totalWeight;
                if (share >= limit[i] - wan[i]) saturated.Add(i);
            }
            if (saturated.Count == 0)
            {
                foreach (var i in active) wan[i] += remaining * weights[i] / totalWeight;
                return 0;
            }
            foreach (var i in saturated)
            {
                remaining -= limit[i] - wan[i];
                wan[i] = limit[i];
                active.Remove(i);
            }
        }
        return Math.Max(0, remaining);
    }
}

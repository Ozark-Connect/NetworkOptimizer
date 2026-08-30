namespace NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;

/// <summary>
/// Splits a WAN's measured rate across the clients that could have produced it. There is no
/// per-client WAN counter at second resolution, so this is a reconciliation, per direction:
/// <list type="number">
/// <item>The clients' rates add up to the WAN rate within <see cref="Threshold"/>: the WAN explains
/// the whole load, nothing is local, every client's WAN rate is its measured rate.</item>
/// <item>They exceed it: some traffic never left the site. The WAN rate is water-filled across
/// clients in proportion to their recent DPI WAN bytes, each capped by its measured rate and by
/// what its uplink chain carried. A client with no recent DPI bytes gets none; when nobody has
/// any (DPI unavailable) the measured rates weight it instead.</item>
/// </list>
/// </summary>
public static class WanShareReconciler
{
    /// <summary>How far past the WAN rate the clients' total may run before some of it is called local.</summary>
    public const double Threshold = 0.15;

    /// <summary>One client's measured rate, its DPI WAN bytes over the recent window, and the
    /// least its uplink chain carried (null when no hop had a rate).</summary>
    public readonly record struct Load(double RateBps, double DpiBytes, double? ChainCapBps);

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

        var active = new List<int>();
        for (var i = 0; i < n; i++)
            if (weights[i] > 0 && caps[i] > 0) active.Add(i);

        var remaining = wanRateBps;
        while (active.Count > 0 && remaining > 0)
        {
            double totalWeight = 0;
            foreach (var i in active) totalWeight += weights[i];
            if (totalWeight <= 0) break;

            // Anyone whose share overflows their cap takes the cap; the rest is re-shared.
            var saturated = new List<int>();
            foreach (var i in active)
            {
                var share = remaining * weights[i] / totalWeight;
                if (share >= caps[i]) saturated.Add(i);
            }
            if (saturated.Count == 0)
            {
                foreach (var i in active) wan[i] = remaining * weights[i] / totalWeight;
                break;
            }
            foreach (var i in saturated)
            {
                wan[i] = caps[i];
                remaining -= caps[i];
                active.Remove(i);
            }
        }
        return new Split(wan, true);
    }
}

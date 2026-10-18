namespace NetworkOptimizer.Monitoring.WanFlood;

/// <summary>The detector's thresholds. The defaults are the ones the spec validated.</summary>
public sealed record FloodThresholds
{
    /// <summary>Softirq share of one receive CPU, in percent, that counts as overloaded.</summary>
    public int SoftirqPercent { get; init; } = 85;
    /// <summary>Unsolicited new connections per second (dropped, never confirmed) that make it a flood.</summary>
    public int UnsolicitedPps { get; init; } = 5000;
    /// <summary>Least share of the unsolicited drops a source must carry to be dropped.</summary>
    public double MinShare { get; init; } = 0.10;
    /// <summary>Least share a single destination port must carry for the no-dominant-source path.</summary>
    public double PortShare { get; init; } = 0.5;
    public int ConntrackFillPercent { get; init; } = 80;
    public int ConntrackClearPercent { get; init; } = 60;
    /// <summary>Packets a drop must still see in the last minute to have its time to live extended.</summary>
    public long ExtendPackets { get; init; } = 1000;
}

/// <summary>One second of receive-path load.</summary>
public sealed record LoadSample(
    DateTime At,
    IReadOnlyDictionary<int, double> SoftirqPercent,
    IReadOnlyDictionary<int, long> DroppedDelta,
    IReadOnlyDictionary<int, long> SqueezeDelta,
    double UnsolicitedPps);

/// <summary>Whether the receive path is under a flood right now, and on which CPU.</summary>
public readonly record struct TriggerResult(bool Suspected, int HotCpu, double PeakSoftirq);

/// <summary>
/// The flood trigger over the last five one-second samples: one receive CPU overloaded in three of them,
/// that CPU falling behind (backlog drops, or squeezes in three samples), and enough unsolicited new
/// connections. A large download can load a CPU but carries no unsolicited packets, so it never triggers.
/// </summary>
public sealed class FloodTrigger
{
    private const int Window = 5;
    private const int Needed = 3;
    private readonly Queue<LoadSample> _samples = new();

    public void Add(LoadSample sample)
    {
        _samples.Enqueue(sample);
        while (_samples.Count > Window) _samples.Dequeue();
    }

    public void Clear() => _samples.Clear();

    /// <param name="receiveCpus">The CPUs the WAN's packets are steered to; empty means every CPU.</param>
    public TriggerResult Evaluate(FloodThresholds t, IReadOnlySet<int> receiveCpus)
    {
        if (_samples.Count < Needed) return default;
        var latest = _samples.Last();
        var cpus = latest.SoftirqPercent.Keys.Where(c => receiveCpus.Count == 0 || receiveCpus.Contains(c));
        var best = default(TriggerResult);
        foreach (var cpu in cpus)
        {
            var loaded = _samples.Count(s => s.SoftirqPercent.GetValueOrDefault(cpu) >= t.SoftirqPercent);
            var dropped = _samples.Sum(s => s.DroppedDelta.GetValueOrDefault(cpu)) > 0;
            var squeezed = _samples.Count(s => s.SqueezeDelta.GetValueOrDefault(cpu) > 0) >= Needed;
            var peak = _samples.Max(s => s.SoftirqPercent.GetValueOrDefault(cpu));
            if (loaded >= Needed && (dropped || squeezed) && peak > best.PeakSoftirq)
                best = new TriggerResult(true, cpu, peak);
        }
        return best.Suspected && latest.UnsolicitedPps >= t.UnsolicitedPps ? best : best with { Suspected = false };
    }

    /// <summary>The highest softirq share of any CPU in the window, for incident reports.</summary>
    public (int Cpu, double Percent) Hottest(IReadOnlySet<int> receiveCpus)
    {
        var best = (Cpu: -1, Percent: 0.0);
        foreach (var s in _samples)
            foreach (var (cpu, pct) in s.SoftirqPercent)
                if ((receiveCpus.Count == 0 || receiveCpus.Contains(cpu)) && pct > best.Percent)
                    best = (cpu, pct);
        return best;
    }
}

/// <summary>A candidate for a drop, measured at WAN ingress over the attribution window.</summary>
public sealed record FloodCandidate(string Wan, string? Source, string Destination, string Protocol, int Port, long Packets)
{
    /// <summary>Why the candidate may not be dropped, or null when it may.</summary>
    public string? Refusal { get; init; }
}

/// <summary>Picks what to drop from measured candidates, by their share of the unsolicited drops.</summary>
public static class FloodAttribution
{
    /// <summary>
    /// The candidates to drop: not refused, and each carrying at least <see cref="FloodThresholds.MinShare"/>
    /// of the unsolicited packets seen over the same window. Never ranked by rate alone: a download is the top
    /// talker and carries none of the unsolicited drops, and the conntrack refusal already removed real sessions.
    /// </summary>
    public static List<(FloodCandidate Candidate, double Share)> ChooseSources(IEnumerable<FloodCandidate> measured,
        double unsolicitedPackets, FloodThresholds t, int maxCount)
    {
        if (unsolicitedPackets <= 0) return [];
        return measured
            .Where(c => c.Refusal == null && c.Source != null)
            .Select(c => (Candidate: c, Share: Math.Min(1.0, c.Packets / unsolicitedPackets)))
            .Where(x => x.Share >= t.MinShare)
            .OrderByDescending(x => x.Share)
            .Take(maxCount)
            .ToList();
    }

    /// <summary>A port carries the flood when it holds at least <see cref="FloodThresholds.PortShare"/> of it.</summary>
    public static bool PortDominates(long portPackets, double unsolicitedPackets, FloodThresholds t) =>
        unsolicitedPackets > 0 && portPackets / unsolicitedPackets >= t.PortShare;
}

/// <summary>What a drop's time to live does at expiry.</summary>
public enum TtlAction { Keep, Extend, Remove }

/// <summary>Time-to-live policy for the detector's drops and limits.</summary>
public static class FloodTtl
{
    /// <summary>
    /// At expiry a drop that still sees traffic is extended, up to <paramref name="maxTtl"/> after it was
    /// applied; past that it is removed and the detector re-attributes a flood that is still there.
    /// </summary>
    public static TtlAction Decide(DateTime now, DateTime appliedAt, DateTime expiresAt, long packetsLastMinute,
        TimeSpan maxTtl, FloodThresholds t)
    {
        if (now < expiresAt) return TtlAction.Keep;
        if (now - appliedAt >= maxTtl) return TtlAction.Remove;
        return packetsLastMinute >= t.ExtendPackets ? TtlAction.Extend : TtlAction.Remove;
    }
}

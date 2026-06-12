using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>Builders for synthetic latency and throughput series.</summary>
internal static class TestSeries
{
    public static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One sample per minute for the given duration at constant values.</summary>
    public static List<LatencySample> Flat(DateTime start, TimeSpan duration, double rttMs, double jitterMs, double lossPct = 0)
    {
        var samples = new List<LatencySample>();
        for (var t = start; t < start + duration; t = t.AddMinutes(1))
        {
            samples.Add(new LatencySample(t, rttMs, rttMs + jitterMs, jitterMs, lossPct));
        }
        return samples;
    }

    /// <summary>Replaces samples within [from, to) with the given values.</summary>
    public static List<LatencySample> WithSegment(this List<LatencySample> samples, DateTime from, DateTime to, double rttMs, double jitterMs, double? lossPct = null)
    {
        return samples
            .Select(s => s.Time >= from && s.Time < to
                ? new LatencySample(s.Time, rttMs, rttMs + jitterMs, jitterMs, lossPct ?? s.LossPercent)
                : s)
            .ToList();
    }

    public static AsnSeries Asn(int asn, string name, List<LatencySample> samples, params string[] targetIds) => new()
    {
        AsnNumber = asn,
        AsnName = name,
        TargetIds = targetIds.Length > 0 ? targetIds.ToList() : new List<string> { $"target-as{asn}" },
        Samples = samples
    };

    /// <summary>One throughput point per minute at constant rates in Mbps.</summary>
    public static List<ThroughputSample> Throughput(DateTime start, TimeSpan duration, double downMbps, double upMbps)
    {
        var samples = new List<ThroughputSample>();
        for (var t = start; t < start + duration; t = t.AddMinutes(1))
        {
            samples.Add(new ThroughputSample(t, downMbps * 1_000_000, upMbps * 1_000_000));
        }
        return samples;
    }
}

using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The counter-delta rule, shared by the fold that stores a point and the collector that feeds the
/// live cache. Both read the same counters, so the rule is tested once here rather than twice.
/// </summary>
public class ApAgentThroughputTests
{
    private static readonly DateTime At = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RateIsTheDeltaOverTheRealGap()
    {
        // 1,250,000 bytes in 10 s is 1 Mbps.
        var (tx, rx) = ApAgentThroughput.FromCounters(
            txBytes: 1_250_000, rxBytes: 2_500_000, at: At.AddSeconds(10),
            priorTxBytes: 0, priorRxBytes: 0, priorAt: At);

        Assert.Equal(1_000_000, tx!.Value, 0);
        Assert.Equal(2_000_000, rx!.Value, 0);
    }

    [Fact]
    public void TheGapIsWhatIsMeasured_NotAnAssumedInterval()
    {
        // The same delta over twice the time is half the rate. Dating a reading wrongly is what
        // produced an oversized spike followed by zeroes.
        var (tenSeconds, _) = ApAgentThroughput.FromCounters(1_250_000, 0, At.AddSeconds(10), 0, 0, At);
        var (twentySeconds, _) = ApAgentThroughput.FromCounters(1_250_000, 0, At.AddSeconds(20), 0, 0, At);

        Assert.Equal(tenSeconds!.Value / 2, twentySeconds!.Value, 0);
    }

    [Fact]
    public void ACounterGoingBackwardsYieldsNothing()
    {
        // An association reset, not negative traffic.
        var (tx, rx) = ApAgentThroughput.FromCounters(10, 10, At.AddSeconds(10), 5_000_000, 5_000_000, At);

        Assert.Null(tx);
        Assert.Null(rx);
    }

    [Fact]
    public void TooLittleTimeYieldsNothingRatherThanAHugeRate()
    {
        // Dividing by a gap this small amplifies the reading's own timing error into a rate far
        // larger than any traffic that occurred.
        var (tx, rx) = ApAgentThroughput.FromCounters(1_250_000, 1_250_000, At.AddMilliseconds(100), 0, 0, At);

        Assert.Null(tx);
        Assert.Null(rx);
    }

    [Fact]
    public void AnIdleClientReportsZeroRatherThanNothing()
    {
        // Zero is a real reading: the client is connected and moved nothing. It must be
        // distinguishable from "could not be measured", which is what null means.
        var (tx, rx) = ApAgentThroughput.FromCounters(5_000, 5_000, At.AddSeconds(10), 5_000, 5_000, At);

        Assert.Equal(0, tx);
        Assert.Equal(0, rx);
    }
}

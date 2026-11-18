using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>The shared traffic-flows records: which span complete reads have covered, and what is kept.</summary>
public class TrafficFlowCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static UniFiTrafficFlow Record(string id, DateTimeOffset at) =>
        new() { Id = id, Time = at.ToUnixTimeMilliseconds(), FlowEndTime = at.ToUnixTimeMilliseconds() };

    [Fact]
    public void ContiguousReadsExtendTheCoveredSpan()
    {
        var cache = new TrafficFlowCache();
        cache.Record(T0, T0.AddMinutes(1), [Record("a", T0.AddSeconds(30))], T0.AddMinutes(1));
        cache.Record(T0.AddMinutes(1), T0.AddMinutes(2), [Record("b", T0.AddSeconds(90))], T0.AddMinutes(2));

        cache.Covered.Should().Be((T0, T0.AddMinutes(2)));
        cache.Since(T0).Select(r => r.Id).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void AReadAfterAGapStartsANewSpan()
    {
        var cache = new TrafficFlowCache();
        cache.Record(T0, T0.AddMinutes(1), [], T0.AddMinutes(1));
        cache.Record(T0.AddMinutes(5), T0.AddMinutes(6), [], T0.AddMinutes(6));

        cache.Covered.Should().Be((T0.AddMinutes(5), T0.AddMinutes(6)), "the minutes between were never read");
    }

    [Fact]
    public void AnOlderReadDoesNotMoveACoveredSpanBack()
    {
        var cache = new TrafficFlowCache();
        cache.Record(T0.AddMinutes(10), T0.AddMinutes(11), [], T0.AddMinutes(11));
        cache.Record(T0, T0.AddMinutes(1), [], T0.AddMinutes(11));

        cache.Covered.Should().Be((T0.AddMinutes(10), T0.AddMinutes(11)));
    }

    [Fact]
    public void RecordsAndCoverageOlderThanKeepAreDropped()
    {
        var cache = new TrafficFlowCache();
        cache.Record(T0, T0.AddMinutes(1), [Record("old", T0.AddSeconds(10))], T0.AddMinutes(1));
        var later = T0 + TrafficFlowCache.Keep + TimeSpan.FromMinutes(5);
        cache.Record(T0.AddMinutes(1), later, [Record("new", later.AddSeconds(-5))], later);

        cache.Since(T0).Select(r => r.Id).Should().Equal("new");
        cache.Covered!.Value.From.Should().Be(later - TrafficFlowCache.Keep);
    }

    [Fact]
    public void ARecordReadTwiceIsKeptOnce()
    {
        var cache = new TrafficFlowCache();
        cache.Record(T0, T0.AddMinutes(1), [Record("a", T0.AddSeconds(30))], T0.AddMinutes(1));
        cache.Record(T0.AddSeconds(30), T0.AddMinutes(2), [Record("a", T0.AddSeconds(30))], T0.AddMinutes(2));

        cache.Since(T0).Should().ContainSingle();
    }
}

using FluentAssertions;
using NetworkOptimizer.Monitoring.WanFlood;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests.WanFlood;

public class WanFloodDetectorCoreTests
{
    private static readonly FloodThresholds T = new();
    private static readonly HashSet<int> Rps = [1, 2, 3];

    private static LoadSample Sample(int second, double cpu2Softirq, long cpu2Dropped, double unsolicitedPps, long squeeze = 0) => new(
        DateTime.UnixEpoch.AddSeconds(second),
        new Dictionary<int, double> { [0] = 40, [1] = 5, [2] = cpu2Softirq, [3] = 5 },
        new Dictionary<int, long> { [2] = cpu2Dropped },
        new Dictionary<int, long> { [2] = squeeze },
        unsolicitedPps);

    private static FloodTrigger Feed(params LoadSample[] samples)
    {
        var trigger = new FloodTrigger();
        foreach (var s in samples) trigger.Add(s);
        return trigger;
    }

    [Fact]
    public void SingleFlowFlood_TriggersOnTheHotCpu()
    {
        var t = Feed(Sample(0, 97, 100, 30000), Sample(1, 98, 200, 30000), Sample(2, 96, 0, 30000));
        var r = t.Evaluate(T, Rps);
        r.Suspected.Should().BeTrue();
        r.HotCpu.Should().Be(2);
    }

    [Fact]
    public void LargeDownload_LoadsTheCpuButNeverTriggers()
    {
        // A download is the top talker but its packets are replies: no unsolicited drops.
        var t = Feed(Sample(0, 97, 50, 20), Sample(1, 98, 50, 20), Sample(2, 96, 50, 20));
        t.Evaluate(T, Rps).Suspected.Should().BeFalse();
    }

    [Fact]
    public void LoadedCpuThatKeepsUp_DoesNotTrigger()
    {
        var t = Feed(Sample(0, 90, 0, 30000), Sample(1, 90, 0, 30000), Sample(2, 90, 0, 30000));
        t.Evaluate(T, Rps).Suspected.Should().BeFalse();
        var squeezed = Feed(Sample(0, 90, 0, 30000, 5), Sample(1, 90, 0, 30000, 5), Sample(2, 90, 0, 30000, 5));
        squeezed.Evaluate(T, Rps).Suspected.Should().BeTrue();
    }

    [Fact]
    public void ShortBurst_DoesNotTrigger()
    {
        var t = Feed(Sample(0, 97, 100, 30000), Sample(1, 30, 0, 30000), Sample(2, 20, 0, 30000), Sample(3, 20, 0, 30000));
        t.Evaluate(T, Rps).Suspected.Should().BeFalse();
    }

    [Fact]
    public void CpuOutsideTheReceiveMask_IsIgnored()
    {
        var t = Feed(Sample(0, 97, 100, 30000), Sample(1, 98, 100, 30000), Sample(2, 99, 100, 30000));
        t.Evaluate(T, new HashSet<int> { 1, 3 }).Suspected.Should().BeFalse();
        t.Evaluate(T, new HashSet<int>()).Suspected.Should().BeTrue();
    }

    // Live finding: the per-source limit holds a single source at 40-62% of one core, and a multi-source flood
    // spreads to ~40% per core while losing packets at the ingress shaper. Neither reaches the CPU path.
    private static LoadSample Spread(int second, double unsolicited, double shaperDrops = 0) => new(
        DateTime.UnixEpoch.AddSeconds(second),
        new Dictionary<int, double> { [1] = 36, [2] = 42, [3] = 39 },
        new Dictionary<int, long>(),
        new Dictionary<int, long>(),
        unsolicited,
        shaperDrops);

    [Fact]
    public void SustainedUnsolicitedDrops_TriggerWithNoCoreOverloaded()
    {
        var t = Feed(Spread(0, 65000), Spread(1, 65000), Spread(2, 65000), Spread(3, 65000), Spread(4, 65000));
        var r = t.Evaluate(T, Rps);
        r.Suspected.Should().BeTrue();
        r.Reason.Should().Be("rate");
        r.HotCpu.Should().Be(2);
    }

    [Fact]
    public void ShortUnsolicitedSpike_DoesNotTrigger()
    {
        var t = Feed(Spread(0, 200), Spread(1, 65000), Spread(2, 65000), Spread(3, 65000), Spread(4, 65000));
        t.Evaluate(T, Rps).Suspected.Should().BeFalse();
    }

    [Fact]
    public void ShaperLossWithUnsolicitedDrops_Triggers()
    {
        var t = Feed(Spread(0, 9000, 30000), Spread(1, 9000, 30000), Spread(2, 9000, 30000));
        var r = t.Evaluate(T, Rps);
        r.Suspected.Should().BeTrue();
        r.Reason.Should().Be("shaper");
    }

    [Fact]
    public void ShaperLossFromADownload_NeverTriggers()
    {
        // A saturating download makes fq_codel drop by design but carries no unsolicited connections.
        var t = Feed(Spread(0, 30, 5000), Spread(1, 30, 5000), Spread(2, 30, 5000), Spread(3, 30, 5000), Spread(4, 30, 5000));
        t.Evaluate(T, Rps).Suspected.Should().BeFalse();
    }

    [Fact]
    public void CpuPath_StillReportsItsReason() =>
        Feed(Sample(0, 97, 100, 30000), Sample(1, 98, 200, 30000), Sample(2, 96, 0, 30000)).Evaluate(T, Rps).Reason.Should().Be("cpu");

    [Fact]
    public void Attribution_ByShareOfUnsolicitedDrops_NeverByRate()
    {
        var candidates = new[]
        {
            new FloodCandidate("eth4", "203.0.113.5", "198.51.100.2", "udp", 47000, 50_000),
            new FloodCandidate("eth4", "203.0.113.6", "198.51.100.2", "udp", 47000, 5_000),
            // The busiest flow of all, but it holds a real session: refused.
            new FloodCandidate("eth4", "203.0.113.7", "198.51.100.2", "udp", 443, 900_000) { Refusal = "has a session" },
        };
        var chosen = FloodAttribution.ChooseSources(candidates, 60_000, T, 8);
        chosen.Should().ContainSingle();
        chosen[0].Candidate.Source.Should().Be("203.0.113.5");
        chosen[0].Share.Should().BeApproximately(50_000 / 60_000.0, 0.001);
    }

    [Fact]
    public void Attribution_NothingWithoutUnsolicitedDrops()
    {
        var candidates = new[] { new FloodCandidate("eth4", "203.0.113.5", "198.51.100.2", "udp", 47000, 50_000) };
        FloodAttribution.ChooseSources(candidates, 0, T, 8).Should().BeEmpty();
    }

    [Fact]
    public void PortDominates_AtHalfTheFlood()
    {
        FloodAttribution.PortDominates(60_000, 100_000, T).Should().BeTrue();
        FloodAttribution.PortDominates(40_000, 100_000, T).Should().BeFalse();
    }

    [Fact]
    public void Ttl_ExtendsWhileUsedUpToTheCap()
    {
        var applied = DateTime.UnixEpoch;
        var max = TimeSpan.FromHours(1);
        FloodTtl.Decide(applied.AddMinutes(5), applied, applied.AddMinutes(10), 0, max, T).Should().Be(TtlAction.Keep);
        FloodTtl.Decide(applied.AddMinutes(10), applied, applied.AddMinutes(10), 5000, max, T).Should().Be(TtlAction.Extend);
        FloodTtl.Decide(applied.AddMinutes(10), applied, applied.AddMinutes(10), 10, max, T).Should().Be(TtlAction.Remove);
        FloodTtl.Decide(applied.AddMinutes(61), applied, applied.AddMinutes(60), 5000, max, T).Should().Be(TtlAction.Remove);
    }
}

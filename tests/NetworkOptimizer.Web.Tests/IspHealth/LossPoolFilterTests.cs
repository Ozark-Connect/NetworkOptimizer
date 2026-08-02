using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// The one rule that matters for dropping a target from the loss pool: it is RELATIVE. A target dark
/// while its peers measure is blocked or retired; every target dark at once is an outage, and
/// filtering that would delete the evidence of the thing being scored.
/// </summary>
public class LossPoolFilterTests
{
    private static readonly IspHealthOptions Options = new();

    private static LossPoolFilter.PoolEntry Target(string id, double lossPct, int samples = 60) =>
        new(id, Enumerable.Range(0, samples)
            .Select(i => new LatencySample(TestSeries.Start.AddMinutes(i), 10, 12, 1, lossPct))
            .ToList());

    [Fact]
    public void Flatlined_target_is_dropped_when_peers_are_healthy()
    {
        var pool = new List<LossPoolFilter.PoolEntry>
        {
            Target("dead", 100),
            Target("isp-hop", 0.1),
            Target("dns", 0.0)
        };

        LossPoolFilter.FindFlatlined(pool, Options).Should().BeEquivalentTo(new[] { "dead" });
    }

    [Fact]
    public void Nothing_is_dropped_when_every_target_is_dark()
    {
        // A real WAN outage. Excluding here would filter away exactly the evidence the score exists
        // to reflect, and the window would grade as clean.
        var pool = new List<LossPoolFilter.PoolEntry>
        {
            Target("isp-hop", 100),
            Target("transit", 100),
            Target("dns", 100)
        };

        LossPoolFilter.FindFlatlined(pool, Options).Should().BeEmpty();
    }

    [Fact]
    public void A_merely_lossy_target_stays_in_the_pool()
    {
        // 40% loss is a badly degraded hop and real signal for the score. Only a target reporting
        // nothing usable at all is excluded.
        var pool = new List<LossPoolFilter.PoolEntry> { Target("lossy", 40), Target("isp-hop", 0.2) };

        LossPoolFilter.FindFlatlined(pool, Options).Should().BeEmpty();
    }

    [Fact]
    public void A_target_that_recovered_for_part_of_the_window_stays_in()
    {
        // Dark for most of the window but measuring at the end - that recovery is real data, and it
        // also means the target is reachable, so its loss belongs in the pool.
        var samples = Enumerable.Range(0, 60)
            .Select(i => new LatencySample(TestSeries.Start.AddMinutes(i), 10, 12, 1, i < 50 ? 100 : 0.5))
            .ToList();
        var pool = new List<LossPoolFilter.PoolEntry>
        {
            new("recovered", samples),
            Target("isp-hop", 0.2)
        };

        LossPoolFilter.FindFlatlined(pool, Options).Should().BeEmpty();
    }

    [Fact]
    public void A_barely_reporting_target_is_not_judged()
    {
        // Too few samples to conclude anything - it could be a target that just came online.
        var pool = new List<LossPoolFilter.PoolEntry> { Target("new", 100, samples: 5), Target("isp-hop", 0.2) };

        LossPoolFilter.FindFlatlined(pool, Options).Should().BeEmpty();
    }

    [Fact]
    public void The_pool_is_never_emptied_and_a_lone_target_is_never_dropped()
    {
        LossPoolFilter.FindFlatlined(new List<LossPoolFilter.PoolEntry> { Target("only", 100) }, Options)
            .Should().BeEmpty();

        // Two dark targets and one healthy peer: the healthy peer alone cannot make both others
        // droppable without leaving the pool grading a single target, but it is still a peer, so the
        // guard that matters is that something survives.
        var pool = new List<LossPoolFilter.PoolEntry> { Target("dead-a", 100), Target("dead-b", 100), Target("isp-hop", 0.2) };
        var excluded = LossPoolFilter.FindFlatlined(pool, Options);
        excluded.Should().BeEquivalentTo(new[] { "dead-a", "dead-b" });
        excluded.Count.Should().BeLessThan(pool.Count);
    }
}

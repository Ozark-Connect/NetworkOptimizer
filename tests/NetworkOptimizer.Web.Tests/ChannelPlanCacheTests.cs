using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ChannelPlanCacheTests
{
    private static Dictionary<RadioBand, ChannelPlan> Plan() =>
        new() { [RadioBand.Band2_4GHz] = new ChannelPlan() };

    private static Dictionary<RadioBand, ChannelPlan> Empty() => new();

    [Fact]
    public async Task EmptyPlanIsNotCached()
    {
        // An empty result means the console was unreachable, not that this site has no plan.
        // Caching it would pin "channel analysis unavailable" for the whole TTL.
        var cache = new ChannelPlanCache();
        var builds = 0;

        var first = await cache.GetOrBuildPlanAsync("site|x", false, () =>
        {
            builds++;
            return Task.FromResult(Empty());
        });
        var second = await cache.GetOrBuildPlanAsync("site|x", false, () =>
        {
            builds++;
            return Task.FromResult(Plan());
        });

        first.Should().BeEmpty();
        second.Should().NotBeEmpty("the failed attempt must not have been cached");
        builds.Should().Be(2);
    }

    [Fact]
    public async Task NonEmptyPlanIsCached()
    {
        var cache = new ChannelPlanCache();
        var builds = 0;

        for (var i = 0; i < 3; i++)
            await cache.GetOrBuildPlanAsync("site|x", false, () =>
            {
                builds++;
                return Task.FromResult(Plan());
            });

        builds.Should().Be(1);
    }

    [Fact]
    public async Task ForceRefreshRebuilds()
    {
        var cache = new ChannelPlanCache();
        var builds = 0;
        Task<Dictionary<RadioBand, ChannelPlan>> Build()
        {
            builds++;
            return Task.FromResult(Plan());
        }

        await cache.GetOrBuildPlanAsync("site|x", false, Build);
        await cache.GetOrBuildPlanAsync("site|x", true, Build);

        builds.Should().Be(2);
    }

    [Fact]
    public async Task DifferentOptionSetsDoNotShareAnEntry()
    {
        var cache = new ChannelPlanCache();
        var builds = 0;
        Task<Dictionary<RadioBand, ChannelPlan>> Build()
        {
            builds++;
            return Task.FromResult(Plan());
        }

        await cache.GetOrBuildPlanAsync("site|dfs-include", false, Build);
        await cache.GetOrBuildPlanAsync("site|dfs-avoid", false, Build);

        builds.Should().Be(2, "pinning an AP or changing DFS is a different question, not a stale answer");
    }

    [Fact]
    public async Task CallerMutationDoesNotPoisonTheCache()
    {
        // Channel Analysis clears this dictionary when the user switches back to Show Current
        // Channels. That must not empty the shared entry: every later Recommend Best Channels,
        // and the Dashboard card, would come back empty until the TTL expired.
        var cache = new ChannelPlanCache();
        var first = await cache.GetOrBuildPlanAsync("site|x", false, () => Task.FromResult(Plan()));

        first.Clear();

        var second = await cache.GetOrBuildPlanAsync("site|x", false,
            () => throw new InvalidOperationException("must be served from cache"));
        second.Should().NotBeEmpty("the caller was given a copy, not the cached instance");
    }
}

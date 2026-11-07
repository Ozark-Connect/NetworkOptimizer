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
    public async Task InvalidateSite_DropsEveryOptionSetForThatSiteOnly()
    {
        var cache = new ChannelPlanCache();
        var builds = new Dictionary<string, int>();
        Task<Dictionary<RadioBand, ChannelPlan>> BuildFor(string key)
        {
            builds[key] = builds.GetValueOrDefault(key) + 1;
            return Task.FromResult(Plan());
        }
        var keys = new[] { "site|Exclude|False|none|kept:none", "site|IncludeWithPenalty|False|none|kept:none", "site2|Exclude|False|none|kept:none" };
        foreach (var key in keys)
            await cache.GetOrBuildPlanAsync(key, false, () => BuildFor(key));

        cache.InvalidateSite("site");
        foreach (var key in keys)
            await cache.GetOrBuildPlanAsync(key, false, () => BuildFor(key));

        builds[keys[0]].Should().Be(2);
        builds[keys[1]].Should().Be(2);
        builds[keys[2]].Should().Be(1, "another site's plan, even one whose slug starts the same, is untouched");
    }

    [Fact]
    public async Task RadioHours_ConcurrentCallersShareOneFetch_AndEachSiteHasItsOwn()
    {
        var cache = new ChannelPlanCache();
        var fetches = new Dictionary<string, int>();
        var release = new TaskCompletionSource();
        Func<Task<IReadOnlyDictionary<(string Mac, RadioBand Band), (double Avg, double Peak)>>> FetchFor(string site) => async () =>
        {
            lock (fetches) fetches[site] = fetches.GetValueOrDefault(site) + 1;
            await release.Task;
            return new Dictionary<(string, RadioBand), (double, double)> { [("aa:bb:cc:dd:ee:01", RadioBand.Band2_4GHz)] = (site == "a" ? 70 : 20, 90) };
        };

        var callers = Enumerable.Range(0, 5).Select(_ => cache.GetOrBuildRadioHoursAsync("a", FetchFor("a")))
            .Append(cache.GetOrBuildRadioHoursAsync("b", FetchFor("b")))
            .ToList();
        release.SetResult();
        var results = await Task.WhenAll(callers);

        fetches.Should().Equal(new Dictionary<string, int> { ["a"] = 1, ["b"] = 1 });
        results.Take(5).Should().AllSatisfy(r => r[("aa:bb:cc:dd:ee:01", RadioBand.Band2_4GHz)].Avg.Should().Be(70));
        results[5][("aa:bb:cc:dd:ee:01", RadioBand.Band2_4GHz)].Avg.Should().Be(20);

        // Inside the TTL a later caller gets the cached values without a fetch.
        await cache.GetOrBuildRadioHoursAsync("a", FetchFor("a"));
        fetches["a"].Should().Be(1);
    }

    [Fact]
    public async Task QuietestChannels_AreCachedPerSite()
    {
        var cache = new ChannelPlanCache();
        var fetches = 0;
        Task<IReadOnlyDictionary<(string Mac, RadioBand Band), int>> Fetch()
        {
            fetches++;
            return Task.FromResult<IReadOnlyDictionary<(string Mac, RadioBand Band), int>>(new Dictionary<(string, RadioBand), int>());
        }

        await cache.GetOrBuildQuietestChannelsAsync("a", Fetch);
        await cache.GetOrBuildQuietestChannelsAsync("a", Fetch);
        await cache.GetOrBuildQuietestChannelsAsync("b", Fetch);

        fetches.Should().Be(2, "an empty answer is cached too, and site b asks once for itself");
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

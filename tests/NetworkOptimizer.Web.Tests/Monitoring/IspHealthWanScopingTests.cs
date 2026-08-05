using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// ISP Health now scopes every input to the WAN it grades. These pin the scoping predicates
/// themselves - which targets a WAN owns, which Influx wan-tag filter each scope emits, and
/// how the primary's wan key resolves - plus the single-WAN equivalence bar: with one WAN and
/// no contexts, the scoped selection must be exactly what the old unscoped queries returned.
/// </summary>
public class IspHealthWanScopingTests
{
    private static MonitoringTarget Target(string id, string? wan) => new()
    {
        TargetId = id,
        Name = id,
        Address = "192.0.2.1",
        WanInterface = wan,
    };

    // ─── Target scoping ───

    [Fact]
    public void PrimaryScope_KeepsItsOwnAndUnstampedRows()
    {
        var targets = new List<MonitoringTarget>
        {
            Target("a", null),          // hand-added / legacy - always a primary-path measurement
            Target("b", ""),
            Target("c", "wan"),
            Target("d", "WAN"),         // key case is not a different WAN
            Target("e", "wan2"),        // another WAN's row must never grade the primary
        };

        IspHealthService.ScopeTargetsToWan(targets, "wan", includeUnassigned: true)
            .Select(t => t.TargetId).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void ScopedWan_OwnsOnlyRowsStampedWithItsKey()
    {
        var targets = new List<MonitoringTarget>
        {
            Target("a", null),          // unstamped belongs to the primary, not to wan2
            Target("b", "wan"),
            Target("c", "wan2"),
            Target("d", "WAN2"),
            Target("e", "wan2"),
        };

        IspHealthService.ScopeTargetsToWan(targets, "wan2", includeUnassigned: false)
            .Select(t => t.TargetId).Should().Equal("c", "d", "e");
    }

    [Fact]
    public void SingleWanSite_ScopedSelectionIsExactlyTheOldUnscopedOne()
    {
        // The equivalence bar: a single-WAN site's rows are unstamped (legacy/hand-added) or
        // stamped with its one wan key, so the primary scope selects every row the old
        // unfiltered query returned - same rows, same order.
        var targets = new List<MonitoringTarget>
        {
            Target("legacy", null),
            Target("hop", "wan"),
            Target("transit", "wan"),
            Target("dns", null),
        };

        var scoped = IspHealthService.ScopeTargetsToWan(targets, "wan", includeUnassigned: true);

        scoped.Should().Equal(targets);
    }

    // ─── Primary wan key resolution ───

    [Fact]
    public void PrimaryWanKey_FallsBackToTheConventionalWanWithNoContexts()
    {
        IspHealthService.ResolvePrimaryWanKey(Array.Empty<WanDiscoveryContext>()).Should().Be("wan");
    }

    [Fact]
    public void PrimaryWanKey_PrefersTheWanRowOverOthers()
    {
        var contexts = new[]
        {
            new WanDiscoveryContext { WanInterface = "wan2" },
            new WanDiscoveryContext { WanInterface = "wan" },
        };
        IspHealthService.ResolvePrimaryWanKey(contexts).Should().Be("wan");
    }

    [Fact]
    public void PrimaryWanKey_TakesTheOnlyRowWhenWanIsAbsent()
    {
        var contexts = new[] { new WanDiscoveryContext { WanInterface = "wan2" } };
        IspHealthService.ResolvePrimaryWanKey(contexts).Should().Be("wan2");
    }

    // ─── Influx wan-tag scope ───

    [Fact]
    public void PrimaryScope_WithNoContextsReadsOnlyUntaggedSeries()
    {
        var scope = IspHealthService.BuildWanScope(Array.Empty<WanContext>(), "wan", primaryScope: true);

        scope.IncludeUntagged.Should().BeTrue();
        scope.WanTags.Should().BeEmpty();
    }

    [Fact]
    public void PrimaryScope_IgnoresContextsBoundToOtherWans()
    {
        var contexts = new[] { new WanContext { Name = "backup", WanInterface = "wan2" } };

        var scope = IspHealthService.BuildWanScope(contexts, "wan", primaryScope: true);

        scope.WanTags.Should().BeEmpty();
    }

    [Fact]
    public void PrimaryScope_KeepsAPrimaryBoundContextsTaggedPoints()
    {
        var contexts = new[] { new WanContext { Name = "gw-bound", WanInterface = "wan" } };

        var scope = IspHealthService.BuildWanScope(contexts, "wan", primaryScope: true);

        scope.IncludeUntagged.Should().BeTrue();
        scope.WanTags.Should().BeEquivalentTo("wan", "gw-bound");
    }

    [Fact]
    public void ScopedWan_ReadsItsKeyAndItsContextsNames_NeverUntagged()
    {
        var contexts = new[]
        {
            new WanContext { Name = "starlink-backup", WanInterface = "wan2" },
            new WanContext { Name = "other", WanInterface = "wan3" },
        };

        var scope = IspHealthService.BuildWanScope(contexts, "wan2", primaryScope: false);

        scope.IncludeUntagged.Should().BeFalse();
        scope.WanTags.Should().BeEquivalentTo("wan2", "starlink-backup");
    }

    [Fact]
    public void ScopedWan_WithNoContextRowStillFiltersOnItsStableKey()
    {
        var scope = IspHealthService.BuildWanScope(Array.Empty<WanContext>(), "wan2", primaryScope: false);

        scope.IncludeUntagged.Should().BeFalse();
        scope.WanTags.Should().Equal("wan2");
    }
}

using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// The Flux filter stage a latency wan-scope emits. The shapes are a correctness AND
/// performance contract (see BuildWanScopeFilter's remarks): tag ABSENCE for the primary -
/// never an empty-string equality, which matches nothing against a series that has no wan
/// column - and plain pushdown-safe tag equality for a scoped WAN.
/// </summary>
public class WanScopeFilterTests
{
    [Fact]
    public void NoScope_EmitsNoFilterStage()
    {
        MonitoringInfluxClient.BuildWanScopeFilter(null).Should().BeEmpty();
    }

    [Fact]
    public void PrimaryWithNoContexts_FiltersOnTagAbsenceOnly()
    {
        var filter = MonitoringInfluxClient.BuildWanScopeFilter(
            MonitoringInfluxClient.LatencyWanScope.Primary());

        filter.Should().Be("\n  |> filter(fn: (r) => not exists r.wan)");
    }

    [Fact]
    public void PrimaryWithAPrimaryBoundContext_KeepsBothShapesInOnePredicate()
    {
        var filter = MonitoringInfluxClient.BuildWanScopeFilter(
            MonitoringInfluxClient.LatencyWanScope.Primary(new[] { "wan" }));

        filter.Should().Be(@"
  |> filter(fn: (r) => not exists r.wan or r.wan == ""wan"")");
    }

    [Fact]
    public void ScopedWan_IsAPlainTagEqualityChain()
    {
        var filter = MonitoringInfluxClient.BuildWanScopeFilter(
            MonitoringInfluxClient.LatencyWanScope.ForWan(new[] { "wan2", "starlink-backup" }));

        filter.Should().Be(@"
  |> filter(fn: (r) => r.wan == ""wan2"" or r.wan == ""starlink-backup"")");
    }

    [Fact]
    public void ScopedWan_DeduplicatesTagValues()
    {
        var filter = MonitoringInfluxClient.BuildWanScopeFilter(
            MonitoringInfluxClient.LatencyWanScope.ForWan(new[] { "wan2", "wan2" }));

        filter.Should().Be("\n  |> filter(fn: (r) => r.wan == \"wan2\")");
    }

    [Fact]
    public void ScopedWanWithNoUsableTags_MatchesNothingRatherThanEveryWan()
    {
        var filter = MonitoringInfluxClient.BuildWanScopeFilter(
            MonitoringInfluxClient.LatencyWanScope.ForWan(new[] { "" }));

        filter.Should().Contain("exists r.wan and not exists r.wan");
    }

    [Fact]
    public void TagValues_AreFluxSanitized()
    {
        var filter = MonitoringInfluxClient.BuildWanScopeFilter(
            MonitoringInfluxClient.LatencyWanScope.ForWan(new[] { "a\"b" }));

        filter.Should().NotContain("a\"b");
    }
}

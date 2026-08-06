using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Unpinned targets probe the WAN the box leaves by. Once a vantage measures that WAN they belong
/// to it, or they sit in a bucket its own report cannot see. Only where unpinned honestly meant
/// that WAN, which is every failover site and no load-balancing one without a route to prove it.
/// </summary>
public class PrimaryWanVantageAdoptionTests
{
    private static NetworkOptimizerDbContext NewDb() =>
        new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MonitoringTarget Target(string id, string? wan, int? contextId = null,
        MonitoringTargetType type = MonitoringTargetType.Custom) => new()
        {
            TargetId = id,
            Name = id,
            Address = "203.0.113.10",
            TargetType = type,
            WanInterface = wan,
            WanContextId = contextId
        };

    [Theory]
    [InlineData(false, false, true)]   // failover: unpinned meant the primary
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]     // load balancing, but a route pins the box
    [InlineData(true, false, false)]   // load balancing with nothing to prove it
    public void ShouldAdopt_only_where_unpinned_honestly_meant_this_wan(
        bool loadBalances, bool routePins, bool expected)
    {
        PrimaryWanVantageAdoption.ShouldAdopt(loadBalances, routePins).Should().Be(expected);
    }

    [Theory]
    [InlineData("wan", "wan", true)]
    [InlineData("wan1", "wan", true)]   // the legacy alias is the same WAN
    [InlineData("wan3", "wan3", true)]  // a WAN3-primary site: no key is privileged
    [InlineData("wan2", "wan", false)]
    [InlineData("wan", null, false)]    // unknown primary claims nothing
    public void MeasuresPrimaryWan_compares_normalized_keys(
        string vantageWan, string? primaryKey, bool expected)
    {
        var vantage = new WanContext { Id = 1, Name = "v", WanInterface = vantageWan };

        PrimaryWanVantageAdoption.MeasuresPrimaryWan(vantage, primaryKey).Should().Be(expected);
    }

    [Fact]
    public async Task Adopts_unpinned_targets_and_leaves_everything_else()
    {
        await using var db = NewDb();
        var vantage = new WanContext { Id = 7, Name = "Fiber", WanInterface = "wan" };
        db.MonitoringTargets.AddRange(
            Target("legacy-null", null),
            Target("unpinned", MonitoringTarget.UnpinnedWan),
            Target("gateway", MonitoringTarget.UnpinnedWan, type: MonitoringTargetType.Fabric),
            Target("other-wan", "wan3"),
            Target("already-assigned", MonitoringTarget.UnpinnedWan, contextId: 9));
        await db.SaveChangesAsync();

        (await PrimaryWanVantageAdoption.AdoptUnpinnedTargetsAsync(db, vantage)).Should().Be(2);
        await db.SaveChangesAsync();

        var byId = await db.MonitoringTargets.ToDictionaryAsync(t => t.TargetId);
        byId["legacy-null"].WanContextId.Should().Be(7);
        byId["legacy-null"].WanInterface.Should().Be("wan");
        byId["unpinned"].WanContextId.Should().Be(7);

        // Fabric never leaves the LAN, so no WAN describes it.
        byId["gateway"].WanContextId.Should().BeNull();
        byId["gateway"].WanInterface.Should().Be(MonitoringTarget.UnpinnedWan);
        byId["other-wan"].WanInterface.Should().Be("wan3");
        byId["already-assigned"].WanContextId.Should().Be(9);
    }

    [Fact]
    public async Task Adopts_nothing_for_a_vantage_that_was_never_saved()
    {
        await using var db = NewDb();
        db.MonitoringTargets.Add(Target("unpinned", MonitoringTarget.UnpinnedWan));
        await db.SaveChangesAsync();

        var unsaved = new WanContext { Name = "Fiber", WanInterface = "wan" };

        (await PrimaryWanVantageAdoption.AdoptUnpinnedTargetsAsync(db, unsaved)).Should().Be(0);
    }
}

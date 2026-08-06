using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Primary is a role any WAN group can hold, so resolving it is a question about the site rather
/// than about a name. These pin the three sources and, more importantly, that nothing ever falls
/// back to the conventional first WAN: a guessed key gets stamped into rows and outlives the
/// console coming back, which is worse than declining to answer.
/// </summary>
public class PrimaryWanResolverTests
{
    private static NetworkOptimizerDbContext NewDb() =>
        new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Prefers_the_recorded_role_even_when_it_is_not_the_first_wan()
    {
        await using var db = NewDb();
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN", IsPrimary = false });
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN2", IsPrimary = true });
        await db.SaveChangesAsync();

        (await PrimaryWanResolver.ResolveKeyAsync(db)).Should().Be("wan2");
    }

    [Fact]
    public async Task Falls_back_to_the_only_wan_a_single_wan_site_has()
    {
        await using var db = NewDb();
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN2" });
        await db.SaveChangesAsync();

        (await PrimaryWanResolver.ResolveKeyAsync(db)).Should().Be("wan2");
    }

    /// <summary>
    /// A secondary WAN is only ever traced through a context, so a WAN that discovery has run
    /// against but which owns no context was traced by the unbound run - the primary's.
    /// </summary>
    [Fact]
    public async Task Falls_back_to_the_discovered_wan_that_owns_no_context()
    {
        await using var db = NewDb();
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN" });
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN3" });
        db.WanDiscoveryContexts.Add(new WanDiscoveryContext { WanInterface = "wan3" });
        db.WanDiscoveryContexts.Add(new WanDiscoveryContext { WanInterface = "wan" });
        db.WanContexts.Add(new WanContext { Name = "cellular", WanInterface = "wan3" });
        await db.SaveChangesAsync();

        (await PrimaryWanResolver.ResolveKeyAsync(db)).Should().Be("wan");
    }

    [Fact]
    public async Task Declines_rather_than_guessing_when_every_discovered_wan_has_a_context()
    {
        await using var db = NewDb();
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN" });
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN3" });
        db.WanDiscoveryContexts.Add(new WanDiscoveryContext { WanInterface = "wan" });
        db.WanDiscoveryContexts.Add(new WanDiscoveryContext { WanInterface = "wan3" });
        db.WanContexts.Add(new WanContext { Name = "fiber", WanInterface = "wan" });
        db.WanContexts.Add(new WanContext { Name = "cellular", WanInterface = "wan3" });
        await db.SaveChangesAsync();

        (await PrimaryWanResolver.ResolveKeyAsync(db)).Should().BeNull();
    }

    [Fact]
    public async Task Declines_on_a_site_that_knows_nothing_yet()
    {
        await using var db = NewDb();

        (await PrimaryWanResolver.ResolveKeyAsync(db)).Should().BeNull();
    }

    [Fact]
    public async Task Backfill_stamps_every_unstamped_target_and_then_no_ops()
    {
        await using var db = NewDb();
        db.WanProfiles.Add(new WanProfile { WanNetworkgroup = "WAN2", IsPrimary = true });
        db.MonitoringTargets.Add(Target("a", null));
        db.MonitoringTargets.Add(Target("b", ""));
        db.MonitoringTargets.Add(Target("c", "wan3"));
        await db.SaveChangesAsync();

        (await MonitoringTargetWanBackfill.StampUnstampedAsync(db)).Should().Be(2);

        db.MonitoringTargets.Select(t => t.WanInterface).ToList()
            .Should().BeEquivalentTo("wan2", "wan2", "wan3");
        (await MonitoringTargetWanBackfill.StampUnstampedAsync(db)).Should().Be(0);
    }

    [Fact]
    public async Task Backfill_leaves_rows_alone_when_the_primary_cannot_be_resolved()
    {
        await using var db = NewDb();
        db.MonitoringTargets.Add(Target("a", null));
        await db.SaveChangesAsync();

        (await MonitoringTargetWanBackfill.StampUnstampedAsync(db)).Should().Be(0);
        (await db.MonitoringTargets.SingleAsync()).WanInterface.Should().BeNull();
    }

    private static MonitoringTarget Target(string id, string? wan) => new()
    {
        TargetId = id,
        Name = id,
        Address = "203.0.113.10",
        TargetType = MonitoringTargetType.Custom,
        WanInterface = wan
    };
}

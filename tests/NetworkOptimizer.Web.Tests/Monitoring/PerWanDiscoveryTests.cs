using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Upstream discovery used to run for one WAN - the configured primary - so a secondary WAN's
/// context had targets nobody discovered and no hop ancestry to grade. It now runs per context,
/// which puts two things on every target it writes: the WAN the data describes and the context
/// whose agent probes it. These cover that double stamping, the rule that keeps two WANs' runs
/// from fighting over one shared row, and the per-WAN cadence that decides who runs when - each
/// with its no-contexts counterpart, since that is every single-WAN install.
/// </summary>
public class PerWanDiscoveryTests
{
    private static NetworkOptimizerDbContext NewDb() =>
        new(new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AccessHopCandidate Hop(string address) => new()
    {
        TargetId = $"access-{address}",
        Label = "First hop",
        Address = address,
        AsnNumber = 64500,
        AsnName = "Example ISP",
        Role = UpstreamRole.AccessHop,
        HopNumber = 1,
        RespondedTo = ProbeMode.Icmp,
        Method = DiscoveryMethod.DirectRouter,
        Enabled = true,
    };

    private static TransitAsnCandidate Transit(string address) => new()
    {
        AsnNumber = 64501,
        AsnName = "Example Transit",
        Method = DiscoveryMethod.DirectRouter,
        TargetId = $"transit-as64501-{address}",
        HopAddress = address,
        RespondedTo = ProbeMode.Icmp,
        Enabled = true,
    };

    [Fact]
    public async Task ContextRun_StampsBothTheWanAndTheContextOnANewAccessTarget()
    {
        await using var db = NewDb();

        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();

        var target = await db.MonitoringTargets.SingleAsync();
        target.WanInterface.Should().Be("wan2");
        target.WanContextId.Should().Be(4);
    }

    [Fact]
    public async Task ContextRun_StampsBothOnANewTransitTarget()
    {
        await using var db = NewDb();

        await UpstreamTracerService.UpsertTransitTargetAsync(db, Transit("203.0.113.9"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();

        var target = await db.MonitoringTargets.SingleAsync();
        target.WanInterface.Should().Be("wan2");
        target.WanContextId.Should().Be(4);
    }

    [Fact]
    public async Task PrimaryRun_LeavesTheContextAloneJustAsItAlwaysHas()
    {
        // No contexts means no context id, and nothing about the written row changes.
        await using var db = NewDb();

        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan", wanContextId: null, default);
        await db.SaveChangesAsync();

        var target = await db.MonitoringTargets.SingleAsync();
        target.WanInterface.Should().Be("wan");
        target.WanContextId.Should().BeNull();
    }

    [Fact]
    public async Task PrimaryRun_KeepsAHandAssignedContextOnRevalidation()
    {
        // The per-target WAN dropdown is the user's own statement about who probes a target; a
        // primary re-validation that cleared it would silently move the target back.
        await using var db = NewDb();
        db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = "access-198.51.100.1",
            Name = "First hop",
            Address = "198.51.100.1",
            TargetType = MonitoringTargetType.AccessIsp,
            WanInterface = "wan",
            WanContextId = 9,
        });
        await db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan", wanContextId: null, default);
        await db.SaveChangesAsync();

        (await db.MonitoringTargets.SingleAsync()).WanContextId.Should().Be(9);
    }

    [Fact]
    public async Task ContextRun_DoesNotTakeATargetThatAlreadyBelongsToAnotherWan()
    {
        // TargetId is unique, so a path-end both WANs reach is ONE row. Re-homing it every run
        // would have the two WANs trading it back and forth, and each pausing the other's target.
        await using var db = NewDb();
        db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = "access-198.51.100.1",
            Name = "First hop",
            Address = "198.51.100.1",
            TargetType = MonitoringTargetType.AccessIsp,
            WanInterface = "wan",
            Enabled = false,
        });
        await db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();

        var target = await db.MonitoringTargets.SingleAsync();
        target.WanInterface.Should().Be("wan");
        target.WanContextId.Should().BeNull();
        target.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ContextRun_AdoptsARowThatHasNoWanYet()
    {
        await using var db = NewDb();
        db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = "access-198.51.100.1",
            Name = "First hop",
            Address = "198.51.100.1",
            TargetType = MonitoringTargetType.AccessIsp,
        });
        await db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();

        var target = await db.MonitoringTargets.SingleAsync();
        target.WanInterface.Should().Be("wan2");
        target.WanContextId.Should().Be(4);
    }

    [Theory]
    [InlineData(null, "wan", true)]      // never stamped - adoptable, which is every legacy row
    [InlineData("", "wan", true)]
    [InlineData("wan", "wan", true)]
    [InlineData("WAN", "wan", true)]     // the WAN key's case is not a different WAN
    [InlineData("wan2", "wan", false)]
    public void OwnsTargetRow_LetsARunWriteOnlyItsOwnWansRows(string? rowWan, string runWan, bool expected)
    {
        UpstreamTracerService.OwnsTargetRow(rowWan, runWan).Should().Be(expected);
    }

    [Fact]
    public void ContextsDueForDiscovery_SkipsAContextThatHasNoWanYet()
    {
        var contexts = new[] { new WanContext { Id = 1, Name = "backup-wan" } };

        UpstreamRediscoveryService.ContextsDueForDiscovery(
            contexts, new Dictionary<string, DateTime?>(), DateTime.UtcNow, TimeSpan.FromDays(7))
            .Should().BeEmpty();
    }

    [Fact]
    public void ContextsDueForDiscovery_RunsAWanThatHasNeverDiscovered()
    {
        var contexts = new[] { new WanContext { Id = 1, Name = "backup-wan", WanInterface = "wan2" } };

        UpstreamRediscoveryService.ContextsDueForDiscovery(
            contexts, new Dictionary<string, DateTime?>(), DateTime.UtcNow, TimeSpan.FromDays(7))
            .Should().ContainSingle().Which.WanInterface.Should().Be("wan2");
    }

    [Fact]
    public void ContextsDueForDiscovery_HoldsAWanDiscoveredRecentlyAndRunsAStaleOne()
    {
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var contexts = new[]
        {
            new WanContext { Id = 1, Name = "backup-wan", WanInterface = "wan2" },
            new WanContext { Id = 2, Name = "lte", WanInterface = "wan3" },
        };
        var last = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
        {
            ["wan2"] = now.AddDays(-1),
            ["wan3"] = now.AddDays(-9),
        };

        UpstreamRediscoveryService.ContextsDueForDiscovery(contexts, last, now, TimeSpan.FromDays(7))
            .Select(c => c.WanInterface).Should().Equal("wan3");
    }

    [Fact]
    public void ContextsDueForDiscovery_WithNoContextsRunsNothing()
    {
        UpstreamRediscoveryService.ContextsDueForDiscovery(
            Array.Empty<WanContext>(), new Dictionary<string, DateTime?>(), DateTime.UtcNow, TimeSpan.FromDays(7))
            .Should().BeEmpty();
    }

    [Fact]
    public void SelectAgent_WithNoAgentAskedForTakesTheSitesFirst()
    {
        var connections = Connections(1, 2);

        AgentProbeService.SelectAgent(connections, null)!.AgentId.Should().Be(1);
    }

    [Fact]
    public void SelectAgent_TakesTheAgentAskedFor()
    {
        var connections = Connections(1, 2);

        AgentProbeService.SelectAgent(connections, 2)!.AgentId.Should().Be(2);
    }

    [Fact]
    public void SelectAgent_NeverSubstitutesAnotherAgentForTheOneAskedFor()
    {
        // The named agent sits behind a particular WAN; another one measures a different path.
        var connections = Connections(1, 2);

        AgentProbeService.SelectAgent(connections, 99).Should().BeNull();
    }

    private static List<AgentTunnelConnection> Connections(params int[] agentIds)
    {
        var registry = new AgentTunnelRegistry(new AgentTunnelOptions(Enabled: true, Port: 0));
        return agentIds.Select(id => registry.Register(id, "site1", $"Agent{id}")).ToList();
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
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
    public async Task ContextRun_CreatesItsOwnTwinForAHostAnotherWanAlreadyClaimed()
    {
        // A host both WANs reach - a core resolver, a shared ISP hop - is probed from BOTH:
        // the claiming WAN keeps the base row untouched (never re-homed, never re-enabled by
        // the other run), and the second WAN gets its own WAN-qualified row so the two series
        // stay separable and comparable by Address.
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

        var original = await db.MonitoringTargets.SingleAsync(t => t.TargetId == "access-198.51.100.1");
        original.WanInterface.Should().Be("wan");
        original.WanContextId.Should().BeNull();
        original.Enabled.Should().BeFalse();

        var twin = await db.MonitoringTargets.SingleAsync(t => t.TargetId == "access-198.51.100.1@wan2");
        twin.WanInterface.Should().Be("wan2");
        twin.WanContextId.Should().Be(4);
        twin.Enabled.Should().BeTrue();
        twin.Address.Should().Be(original.Address);
    }

    [Fact]
    public async Task ContextRun_RevalidatesItsTwinInsteadOfStackingAnother()
    {
        await using var db = NewDb();
        db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = "access-198.51.100.1",
            Name = "First hop",
            Address = "198.51.100.1",
            TargetType = MonitoringTargetType.AccessIsp,
            WanInterface = "wan",
        });
        await db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();
        await UpstreamTracerService.UpsertTargetAsync(db, Hop("198.51.100.1"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();

        (await db.MonitoringTargets.CountAsync()).Should().Be(2);
        (await db.MonitoringTargets.CountAsync(t => t.WanInterface == "wan2")).Should().Be(1);
    }

    [Fact]
    public async Task ContextRun_CreatesATransitTwinTheSameWay()
    {
        await using var db = NewDb();
        db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = "transit-as64501-203.0.113.9",
            Name = "Example Transit",
            Address = "203.0.113.9",
            TargetType = MonitoringTargetType.Transit,
            WanInterface = "wan",
        });
        await db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTransitTargetAsync(db, Transit("203.0.113.9"), "wan2", wanContextId: 4, default);
        await db.SaveChangesAsync();

        var twin = await db.MonitoringTargets.SingleAsync(t => t.TargetId == "transit-as64501-203.0.113.9@wan2");
        twin.WanInterface.Should().Be("wan2");
        (await db.MonitoringTargets.SingleAsync(t => t.TargetId == "transit-as64501-203.0.113.9"))
            .WanInterface.Should().Be("wan");
    }

    [Fact]
    public void WanQualifiedTargetId_SuffixesTheWanKeyStably()
    {
        UpstreamTracerService.WanQualifiedTargetId("access-198.51.100.1", "WAN2")
            .Should().Be("access-198.51.100.1@wan2");
    }

    /// <summary>
    /// A bound run does not take over an unpinned row - it twins, exactly as it would for a row
    /// stamped with another WAN. Adopting instead is what let a metered secondary claim the site's
    /// hand-added targets wholesale.
    /// </summary>
    [Fact]
    public async Task ContextRun_TwinsRatherThanAdoptingThePrimarysUnstampedRow()
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

        await UpstreamTracerService.UpsertTargetAsync(
            db, Hop("198.51.100.1"), "wan2", wanContextId: 4, default, isUnboundRun: false);
        await db.SaveChangesAsync();

        var primaryRow = await db.MonitoringTargets.SingleAsync(t => t.TargetId == "access-198.51.100.1");
        primaryRow.WanInterface.Should().BeNull();
        primaryRow.WanContextId.Should().BeNull();

        var twin = await db.MonitoringTargets.SingleAsync(t => t.TargetId == "access-198.51.100.1@wan2");
        twin.WanInterface.Should().Be("wan2");
        twin.WanContextId.Should().Be(4);
    }

    /// <summary>
    /// The unbound run still adopts an unpinned row rather than twinning: the single-WAN upgrade
    /// path, where re-running discovery must not leave two rows per hop.
    /// </summary>
    [Fact]
    public async Task PrimaryRun_AdoptsARowThatHasNoWanYet()
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

        await UpstreamTracerService.UpsertTargetAsync(
            db, Hop("198.51.100.1"), "wan", wanContextId: null, default, isUnboundRun: true);
        await db.SaveChangesAsync();

        var target = await db.MonitoringTargets.SingleAsync();
        target.WanInterface.Should().Be("wan");
    }

    [Theory]
    [InlineData(null, "wan", true)]      // never stamped - the primary's, and this IS the primary
    [InlineData("", "wan", true)]
    [InlineData("wan", "wan", true)]
    [InlineData("WAN", "wan", true)]     // the WAN key's case is not a different WAN
    [InlineData("wan2", "wan", false)]
    public void OwnsTargetRow_LetsARunWriteOnlyItsOwnWansRows(string? rowWan, string runWan, bool expected)
    {
        UpstreamTracerService.OwnsTargetRow(rowWan, runWan).Should().Be(expected);
    }

    /// <summary>Unpinned rows belong to the unbound run alone, whatever WAN is committing.</summary>
    [Theory]
    [InlineData(null, "wan3", false, false)]   // a bound (context) run never owns an unstamped row
    [InlineData(null, "wan", false, false)]    // not even when its WAN is the conventional first one
    [InlineData(null, "wan", true, true)]      // the unbound run - the primary's - does
    [InlineData(null, "wan3", true, true)]     // on a WAN3-primary site too: no key is consulted
    [InlineData("wan", "wan3", false, false)]  // an explicit stamp is judged on its own terms
    [InlineData("wan3", "wan3", false, true)]
    [InlineData("wan1", "wan", false, true)]   // the wan1 alias is the same WAN
    public void OwnsTargetRow_GivesUnstampedRowsToTheUnboundRunAlone(
        string? rowWan, string runWan, bool isUnboundRun, bool expected)
    {
        UpstreamTracerService.OwnsTargetRow(rowWan, runWan, isUnboundRun).Should().Be(expected);
    }

    /// <summary>The reported bug: a metered secondary commits and slows only its own rows.</summary>
    [Fact]
    public void SelectTargetsToRepace_LeavesEveryWanButTheMeteredOneAlone()
    {
        var targets = new List<MonitoringTarget>
        {
            Target("ISP speedtest", MonitoringTargetType.AccessIsp, wan: null, interval: 10),
            Target("LAN controller", MonitoringTargetType.Custom, wan: null, interval: 10),
            Target("Transit handoff", MonitoringTargetType.Transit, wan: "wan", interval: 15),
            Target("Satellite first hop", MonitoringTargetType.AccessIsp, wan: "wan2", interval: 15),
            Target("Cellular first hop", MonitoringTargetType.AccessIsp, wan: "wan3", interval: 10),
            Target("Gateway", MonitoringTargetType.Fabric, wan: "wan3", interval: 5),
            Target("Already slow", MonitoringTargetType.Custom, wan: "wan3", interval: 120),
        };

        var repaced = UpstreamTracerService.SelectTargetsToRepace(
            targets, pollIntervalSeconds: 60, wanInterface: "wan3", isUnboundRun: false);

        repaced.Select(t => t.Name).Should().BeEquivalentTo("Cellular first hop");
    }

    /// <summary>Single-WAN: the unbound run's unpinned rows are its own, and slowing them is the point.</summary>
    [Fact]
    public void SelectTargetsToRepace_StillSlowsUnstampedRowsWhenThePrimaryIsTheMeteredWan()
    {
        var targets = new List<MonitoringTarget>
        {
            Target("Public resolver", MonitoringTargetType.InternetService, wan: null, interval: 10),
            Target("First hop", MonitoringTargetType.AccessIsp, wan: "wan", interval: 10),
        };

        var repaced = UpstreamTracerService.SelectTargetsToRepace(
            targets, pollIntervalSeconds: 60, wanInterface: "wan", isUnboundRun: true);

        repaced.Select(t => t.Name).Should().BeEquivalentTo("Public resolver", "First hop");
    }

    private static MonitoringTarget Target(string name, MonitoringTargetType type, string? wan, int interval) => new()
    {
        TargetId = $"custom-{name.Replace(' ', '-')}",
        Name = name,
        Address = "192.0.2.1",
        TargetType = type,
        WanInterface = wan,
        PollIntervalSeconds = interval
    };

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

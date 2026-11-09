using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// What a commit does to an existing target whose candidate came back unchecked. An operator's
/// commit pauses it. An automatic commit leaves it alone when the only reason is that it did not
/// answer, and a manual run started while the WAN was busy keeps it checked for the review.
/// </summary>
public class UpstreamTracerCommitTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _db;

    public UpstreamTracerCommitTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Unreachable_access_hop_pauses_its_row_only_on_an_operator_commit(bool automatic, bool expectPaused)
    {
        Seed("198.51.100.1", MonitoringTargetType.AccessIsp);
        var hop = AccessHop("198.51.100.1", unreachable: true);

        await UpstreamTracerService.PauseUnselectedAccessHopAsync(_db, hop, "wan", isUnboundRun: true, automatic, CancellationToken.None);
        await _db.SaveChangesAsync();

        Row("198.51.100.1").Enabled.Should().Be(!expectPaused);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Unreachable_transit_pauses_its_row_only_on_an_operator_commit(bool automatic, bool expectPaused)
    {
        Seed("203.0.113.1", MonitoringTargetType.Transit);
        var transit = TransitHop("203.0.113.1", unreachable: true);

        await UpstreamTracerService.PauseUnselectedTransitAsync(_db, transit, "wan", isUnboundRun: true, automatic, CancellationToken.None);
        await _db.SaveChangesAsync();

        Row("203.0.113.1").Enabled.Should().Be(!expectPaused);
    }

    [Fact]
    public async Task Automatic_commit_still_pauses_an_off_path_transit()
    {
        Seed("203.0.113.2", MonitoringTargetType.Transit);
        var transit = TransitHop("203.0.113.2", unreachable: true, offPath: true);

        await UpstreamTracerService.PauseUnselectedTransitAsync(_db, transit, "wan", isUnboundRun: true, automatic: true, CancellationToken.None);
        await _db.SaveChangesAsync();

        Row("203.0.113.2").Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Automatic_commit_still_pauses_a_reachable_candidate_left_unchecked()
    {
        Seed("198.51.100.2", MonitoringTargetType.AccessIsp);
        var hop = AccessHop("198.51.100.2", unreachable: false);

        await UpstreamTracerService.PauseUnselectedAccessHopAsync(_db, hop, "wan", isUnboundRun: true, automatic: true, CancellationToken.None);
        await _db.SaveChangesAsync();

        Row("198.51.100.2").Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Busy_run_keeps_monitored_unanswered_candidates_checked()
    {
        Seed("198.51.100.3", MonitoringTargetType.AccessIsp);
        Seed("203.0.113.3", MonitoringTargetType.Transit);
        Seed("203.0.113.4", MonitoringTargetType.Transit, enabled: false);
        var state = new UpstreamTracerState
        {
            WanInterface = "wan",
            AccessHops =
            {
                AccessHop("198.51.100.3", unreachable: true),
                AccessHop("198.51.100.9", unreachable: true), // new candidate: not monitored
            },
            TransitAsns =
            {
                TransitHop("203.0.113.3", unreachable: true),
                TransitHop("203.0.113.4", unreachable: true), // monitored but paused
                TransitHop("203.0.113.5", unreachable: true, offPath: true),
            },
        };
        Seed("203.0.113.5", MonitoringTargetType.Transit);

        var kept = await UpstreamTracerService.KeepMonitoredUnansweredCheckedAsync(_db, state, "wan", isUnboundRun: true, CancellationToken.None);

        kept.Should().Be(2);
        state.AccessHops[0].Enabled.Should().BeTrue();
        state.AccessHops[0].UnansweredWhileBusy.Should().BeTrue();
        state.AccessHops[1].Enabled.Should().BeFalse();
        state.TransitAsns[0].Enabled.Should().BeTrue();
        state.TransitAsns[0].UnansweredWhileBusy.Should().BeTrue();
        state.TransitAsns[1].Enabled.Should().BeFalse();
        state.TransitAsns[2].Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Busy_run_ignores_another_wans_row()
    {
        Seed("198.51.100.4", MonitoringTargetType.AccessIsp, wan: "wan2");
        var state = new UpstreamTracerState { WanInterface = "wan", AccessHops = { AccessHop("198.51.100.4", unreachable: true) } };

        var kept = await UpstreamTracerService.KeepMonitoredUnansweredCheckedAsync(_db, state, "wan", isUnboundRun: false, CancellationToken.None);

        kept.Should().Be(0);
        state.AccessHops[0].Enabled.Should().BeFalse();
    }

    // ---- helpers ----

    private void Seed(string address, MonitoringTargetType type, bool enabled = true, string wan = "wan")
    {
        _db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = $"{type}-{address}-{wan}",
            Name = $"{type} {address}",
            Address = address,
            TargetType = type,
            WanInterface = wan,
            DiscoveryMethod = DiscoveryMethod.DirectRouter,
            Enabled = enabled,
        });
        _db.SaveChanges();
    }

    private MonitoringTarget Row(string address) => _db.MonitoringTargets.AsNoTracking().Single(t => t.Address == address);

    // The reachability gate's own marking: a failed candidate arrives disabled.
    private static AccessHopCandidate AccessHop(string address, bool unreachable) => new()
    {
        TargetId = $"access-{address}",
        Label = $"Access {address}",
        Address = address,
        Method = DiscoveryMethod.DirectRouter,
        Enabled = false,
        Unreachable = unreachable,
    };

    private static TransitAsnCandidate TransitHop(string address, bool unreachable, bool offPath = false) => new()
    {
        AsnNumber = 64497,
        AsnName = "AS64497",
        HopAddress = address,
        Method = DiscoveryMethod.DirectRouter,
        Enabled = false,
        Unreachable = unreachable,
        OffPath = offPath,
    };
}

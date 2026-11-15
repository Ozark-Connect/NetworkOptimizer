using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// An access hop is pinged by ICMP whichever trace found it, so a run finding it the other way must
/// still show, and keep, the operator's saved choice.
/// </summary>
public class UpstreamTracerProbeModeTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _db;

    public UpstreamTracerProbeModeTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    private static AccessHopCandidate Hop(string address, ProbeMode foundBy) => new()
    {
        TargetId = $"access-{address.Replace('.', '-')}",
        Label = "Example 8-2",
        Address = address,
        Method = DiscoveryMethod.DirectRouter,
        RespondedTo = foundBy,
    };

    private static MonitoringTarget Row(string address, ProbeMode mode, bool enabled, string? targetId = null) => new()
    {
        TargetId = targetId ?? $"access-{address.Replace('.', '-')}",
        Name = "Example 8-2",
        Address = address,
        ProbeMode = mode,
        TargetType = MonitoringTargetType.AccessIsp,
        WanInterface = "wan",
        AutoDiscovered = true,
        Enabled = enabled,
    };

    [Theory]
    [InlineData(ProbeMode.Udp, ProbeMode.Icmp)]
    [InlineData(ProbeMode.Icmp, ProbeMode.Udp)]
    public void APausedRow_FoundTheOtherWay_ReviewsAsPaused(ProbeMode saved, ProbeMode foundBy)
    {
        var row = Row("198.51.100.38", saved, enabled: false, targetId: "access-other-id");

        UpstreamTracerService.ReviewRowForAccessHop([row], Hop("198.51.100.38", foundBy))
            .Should().BeSameAs(row);
    }

    [Fact]
    public void TheRowTheCommitWrites_WinsByTargetId()
    {
        var tcp = Row("198.51.100.38", ProbeMode.Tcp, enabled: true, targetId: "access-hand-tcp");
        var own = Row("198.51.100.38", ProbeMode.Udp, enabled: false);

        UpstreamTracerService.ReviewRowForAccessHop([tcp, own], Hop("198.51.100.38", ProbeMode.Icmp))
            .Should().BeSameAs(own);
    }

    [Fact]
    public void ATcpRow_IsADifferentProbe()
    {
        var tcp = Row("198.51.100.38", ProbeMode.Tcp, enabled: false, targetId: "access-hand-tcp");

        UpstreamTracerService.ReviewRowForAccessHop([tcp], Hop("198.51.100.38", ProbeMode.Icmp)).Should().BeNull();
    }

    [Fact]
    public async Task APausedUdpRow_ReviewedAndCommitted_StaysPaused_AndBecomesIcmp()
    {
        _db.MonitoringTargets.Add(Row("198.51.100.38", ProbeMode.Udp, enabled: false));
        await _db.SaveChangesAsync();

        // Review: the checkbox takes the saved row's state.
        var hop = Hop("198.51.100.38", ProbeMode.Icmp);
        var saved = UpstreamTracerService.ReviewRowForAccessHop(await _db.MonitoringTargets.AsNoTracking().ToListAsync(), hop);
        hop.Enabled = saved!.Enabled;

        // Commit: an unchecked hop goes through the pause, a checked one through the upsert.
        if (hop.Enabled)
            await UpstreamTracerService.UpsertTargetAsync(_db, hop, "wan", null, CancellationToken.None);
        else
            await UpstreamTracerService.PauseUnselectedAccessHopAsync(_db, hop, "wan", true, automatic: false, CancellationToken.None);
        await _db.SaveChangesAsync();

        var row = await _db.MonitoringTargets.AsNoTracking().SingleAsync();
        row.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ACheckedHopFoundByUdp_IsSavedAsIcmp()
    {
        _db.MonitoringTargets.Add(Row("198.51.100.38", ProbeMode.Udp, enabled: true));
        await _db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTargetAsync(_db, Hop("198.51.100.38", ProbeMode.Udp), "wan", null, CancellationToken.None);
        await UpstreamTracerService.UpsertTargetAsync(_db, Hop("198.51.100.39", ProbeMode.Udp), "wan", null, CancellationToken.None);
        await _db.SaveChangesAsync();

        (await _db.MonitoringTargets.AsNoTracking().Select(t => t.ProbeMode).ToListAsync())
            .Should().AllBeEquivalentTo(ProbeMode.Icmp);
    }

    [Fact]
    public async Task AHandSetTcpRow_KeepsTcpOnCommit()
    {
        _db.MonitoringTargets.Add(Row("198.51.100.38", ProbeMode.Tcp, enabled: true));
        await _db.SaveChangesAsync();

        await UpstreamTracerService.UpsertTargetAsync(_db, Hop("198.51.100.38", ProbeMode.Icmp), "wan", null, CancellationToken.None);
        await _db.SaveChangesAsync();

        (await _db.MonitoringTargets.AsNoTracking().SingleAsync()).ProbeMode.Should().Be(ProbeMode.Tcp);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// A re-run and commit corrects access rows an earlier run got wrong: rows the new run saw and no
/// longer proposes are paused, on an operator's commit only; rows it never saw and hand-added rows
/// are left alone.
/// </summary>
public class UpstreamTracerUnproposedAccessTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _db;

    public UpstreamTracerUnproposedAccessTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new NetworkOptimizerDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task OperatorCommit_PausesAccessRowsTheRunNoLongerProposes()
    {
        Seed("100.64.0.1");
        Seed("172.18.0.1");      // a container bridge an earlier run committed
        Seed("203.0.113.53");    // a destination an earlier run committed as an access hop

        var paused = await Pause(["100.64.0.1"], automatic: false);

        paused.Should().Be(2);
        Row("100.64.0.1").Enabled.Should().BeTrue();
        Row("172.18.0.1").Enabled.Should().BeFalse();
        Row("203.0.113.53").Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ARowThisRunNeverSaw_IsLeftAlone()
    {
        // A real access hop this run's traces happened to miss (route variance): not evidence.
        Seed("100.64.0.1");
        Seed("198.51.100.18");
        Seed("172.18.0.1");

        var paused = await Pause(["100.64.0.1"], automatic: false, seen: ["100.64.0.1", "172.18.0.1"]);

        paused.Should().Be(1);
        Row("198.51.100.18").Enabled.Should().BeTrue();
        Row("172.18.0.1").Enabled.Should().BeFalse("seen on the traces and filed as our side");
    }

    [Fact]
    public async Task AutomaticCommit_PausesNothing()
    {
        Seed("172.18.0.1");

        (await Pause(["100.64.0.1"], automatic: true)).Should().Be(0);
        Row("172.18.0.1").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ARunThatFoundNoAccessHops_PausesNothing()
    {
        Seed("100.64.0.1");

        (await Pause([], automatic: false)).Should().Be(0);
        Row("100.64.0.1").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task HandAddedRowsAndOtherWansRows_AreLeftAlone()
    {
        Seed("198.51.100.7", autoDiscovered: false);
        Seed("198.51.100.8", wan: "wan2");

        (await Pause(["100.64.0.1"], automatic: false)).Should().Be(0);
        Row("198.51.100.7").Enabled.Should().BeTrue();
        Row("198.51.100.8").Enabled.Should().BeTrue();
    }

    // The review's own edits, through the real commit methods in commit order.

    [Fact]
    public async Task CommitOrder_RenamedChecked_Unchecked_NotProposed_HandAdded()
    {
        Seed("100.64.0.1", enabled: false);   // paused earlier, re-checked and renamed now
        Seed("100.64.0.2");                   // unchecked now
        Seed("100.64.0.3");                   // no longer proposed
        Seed("100.64.0.4", autoDiscovered: false);

        await UpstreamTracerService.UpsertTargetAsync(_db, Hop("100.64.0.1", "Renamed BNG", enabled: true), "wan", null, CancellationToken.None);
        await UpstreamTracerService.PauseUnselectedAccessHopAsync(_db, Hop("100.64.0.2", "Unchecked", enabled: false), "wan", true, automatic: false, CancellationToken.None);
        await Pause(["100.64.0.1", "100.64.0.2"], automatic: false);

        var renamed = Row("100.64.0.1");
        renamed.Enabled.Should().BeTrue("re-checking turns an off target back on");
        renamed.Name.Should().Be("Renamed BNG");
        renamed.TargetId.Should().Be("access-100.64.0.1-wan");
        renamed.TargetType.Should().Be(MonitoringTargetType.AccessIsp);
        Row("100.64.0.2").Enabled.Should().BeFalse("unchecking pauses");
        Row("100.64.0.2").Name.Should().Be("Unchecked", "an unchecked row still takes its inline rename");
        Row("100.64.0.3").Enabled.Should().BeFalse("not proposed by an operator's run");
        Row("100.64.0.4").Enabled.Should().BeTrue("hand-added rows are never touched");
    }

    [Fact]
    public async Task TransitUpsert_RechecksRenamesAndKeepsAnUncheckedPathEndPaused()
    {
        Seed("198.51.100.30", enabled: false, type: MonitoringTargetType.Transit);
        Seed("203.0.113.60", enabled: true, type: MonitoringTargetType.InternetService);

        await UpstreamTracerService.UpsertTransitTargetAsync(_db, new TransitAsnCandidate
        {
            AsnNumber = 64500, AsnName = "Example Transit", Label = "Example Transit edge1",
            Method = DiscoveryMethod.DirectRouter, TargetId = "transit-as64500", HopAddress = "198.51.100.30",
        }, "wan", wanContextId: null, CancellationToken.None);
        await UpstreamTracerService.UpsertTransitTargetAsync(_db, new TransitAsnCandidate
        {
            AsnNumber = 64501, AsnName = "Example CDN", Label = "Example CDN",
            Method = DiscoveryMethod.PathProxy, TargetId = "path-example", PathProxyTarget = "203.0.113.60",
        }, "wan", wanContextId: null, CancellationToken.None, enabled: false);
        await _db.SaveChangesAsync();

        Row("198.51.100.30").Enabled.Should().BeTrue();
        Row("198.51.100.30").Name.Should().Be("Example Transit edge1");
        Row("198.51.100.30").TargetType.Should().Be(MonitoringTargetType.Transit);
        Row("203.0.113.60").Enabled.Should().BeFalse("an unchecked path-end is saved paused");
        Row("203.0.113.60").TargetType.Should().Be(MonitoringTargetType.InternetService);
    }

    private static AccessHopCandidate Hop(string address, string label, bool enabled) => new()
    {
        TargetId = $"access-{address}",
        Label = label,
        Address = address,
        Method = DiscoveryMethod.DirectRouter,
        Enabled = enabled,
    };

    [Fact]
    public async Task AnAccessRowTheRunProposesAsAPathEnd_IsRetypedInPlace_KeepingItsTargetId()
    {
        Seed("203.0.113.53");
        await Pause(["100.64.0.1"], automatic: false);

        await UpstreamTracerService.UpsertTransitTargetAsync(_db, new TransitAsnCandidate
        {
            AsnNumber = 54113,
            AsnName = "Fastly",
            Label = "Fastly",
            Method = DiscoveryMethod.PathProxy,
            TargetId = "path-fastly-as54113",
            PathProxyTarget = "203.0.113.53",
        }, "wan", wanContextId: null, CancellationToken.None);
        await _db.SaveChangesAsync();

        var row = Row("203.0.113.53");
        row.TargetId.Should().Be("access-203.0.113.53-wan", "the history stays filed under it");
        row.TargetType.Should().Be(MonitoringTargetType.InternetService);
        row.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task AHandAddedRow_KeepsItsType()
    {
        Seed("203.0.113.54", autoDiscovered: false);

        await UpstreamTracerService.UpsertTransitTargetAsync(_db, new TransitAsnCandidate
        {
            AsnNumber = 54113,
            AsnName = "Fastly",
            Method = DiscoveryMethod.PathProxy,
            TargetId = "path-fastly-2",
            PathProxyTarget = "203.0.113.54",
        }, "wan", wanContextId: null, CancellationToken.None);
        await _db.SaveChangesAsync();

        Row("203.0.113.54").TargetType.Should().Be(MonitoringTargetType.AccessIsp);
    }

    // The run saw every seeded row on its traces unless told otherwise.
    private async Task<int> Pause(IReadOnlyCollection<string> proposed, bool automatic, IReadOnlyCollection<string>? seen = null)
    {
        seen ??= _db.MonitoringTargets.AsNoTracking().Select(t => t.Address).ToList();
        var count = await UpstreamTracerService.PauseUnproposedAccessRowsAsync(
            _db, proposed, seen, "wan", isUnboundRun: true, automatic, CancellationToken.None);
        await _db.SaveChangesAsync();
        return count;
    }

    private void Seed(string address, bool autoDiscovered = true, string wan = "wan", bool enabled = true,
        MonitoringTargetType type = MonitoringTargetType.AccessIsp)
    {
        _db.MonitoringTargets.Add(new MonitoringTarget
        {
            TargetId = $"access-{address}-{wan}",
            Name = $"Access {address}",
            Address = address,
            TargetType = type,
            WanInterface = wan,
            DiscoveryMethod = autoDiscovered ? DiscoveryMethod.DirectRouter : DiscoveryMethod.UserProvided,
            AutoDiscovered = autoDiscovered,
            Enabled = enabled,
        });
        _db.SaveChanges();
    }

    private MonitoringTarget Row(string address) => _db.MonitoringTargets.AsNoTracking().Single(t => t.Address == address);
}


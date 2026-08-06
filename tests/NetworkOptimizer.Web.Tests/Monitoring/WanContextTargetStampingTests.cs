using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// A monitoring target carries two WAN keys that must never drift apart: WanContextId routes the
/// probe, WanInterface says which WAN the resulting data describes - and every per-WAN reader
/// scopes on the latter. The deploy-time backfill only fixed the rows that existed then, so the
/// three runtime paths that can move one key have to move the other: assigning a target to a
/// context, re-pointing a context at another WAN, and deleting a context. A target that kept a
/// dead or stale WAN stamp reads as flatlined in the primary's report and invisible in its own.
/// </summary>
public class WanContextTargetStampingTests : IDisposable
{
    private readonly string _dir;
    private readonly SiteDbContextFactory _factory;
    private readonly SiteContextService _siteContext;
    private readonly AuditContext _audit = new();

    public WanContextTargetStampingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "no-wan-stamping-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        var paths = new SiteDatabasePaths(Path.Combine(_dir, "network_optimizer.db"));
        _factory = new SiteDbContextFactory(paths);
        _siteContext = new SiteContextService(new HttpContextAccessor(), paths);

        using var db = Db();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; a leftover is harmless */ }
        GC.SuppressFinalize(this);
    }

    private NetworkOptimizerDbContext Db() => _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    private MonitoringTargetService Targets() => new(
        _factory, _siteContext, asnResolution: null!, executorFactory: null!, _audit,
        NullLogger<MonitoringTargetService>.Instance);

    private async Task<int> SeedContextAsync(string name, string? wanInterface)
    {
        await using var db = Db();
        var context = new WanContext
        {
            Name = name,
            WanInterface = wanInterface,
            ProbeSourceIp = "198.51.100.7",
            CreatedAt = DateTime.UtcNow,
        };
        db.WanContexts.Add(context);
        await db.SaveChangesAsync();
        return context.Id;
    }

    private async Task<int> SeedTargetAsync(string targetId, int? contextId = null, string? wanInterface = null)
    {
        await using var db = Db();
        var target = new MonitoringTarget
        {
            TargetId = targetId,
            Name = targetId,
            Address = "203.0.113.10",
            TargetType = MonitoringTargetType.Custom,
            ProbeMode = ProbeMode.Icmp,
            WanContextId = contextId,
            WanInterface = wanInterface,
            CreatedAt = DateTime.UtcNow,
        };
        db.MonitoringTargets.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }

    private async Task<MonitoringTarget> ReadAsync(int id)
    {
        await using var db = Db();
        return (await db.MonitoringTargets.FindAsync(id))!;
    }

    // ─── Path 1: assigning a target to a context ───

    [Fact]
    public async Task Assigning_a_target_to_a_context_stamps_the_contexts_wan()
    {
        var contextId = await SeedContextAsync("backup", "wan2");
        var targetId = await SeedTargetAsync("custom-hop");

        (await Targets().SetWanContextAsync(targetId, contextId)).Should().BeTrue();

        var row = await ReadAsync(targetId);
        row.WanContextId.Should().Be(contextId);
        row.WanInterface.Should().Be("wan2");
    }

    [Fact]
    public async Task Reassigning_a_target_to_another_wans_context_moves_its_stamp_too()
    {
        var backup = await SeedContextAsync("backup", "wan2");
        var lte = await SeedContextAsync("lte", "wan3");
        var targetId = await SeedTargetAsync("custom-hop", backup, "wan2");

        (await Targets().SetWanContextAsync(targetId, lte)).Should().BeTrue();

        var row = await ReadAsync(targetId);
        row.WanContextId.Should().Be(lte);
        row.WanInterface.Should().Be("wan3");
    }

    [Fact]
    public async Task Moving_a_target_back_to_the_primary_clears_both_keys()
    {
        // An unstamped row IS a primary-path measurement to every scoped reader, so the WAN
        // stamp has to go with the routing - a row left saying "wan2" would keep grading the
        // secondary's report with data nothing probes over the secondary any more.
        var contextId = await SeedContextAsync("backup", "wan2");
        var targetId = await SeedTargetAsync("custom-hop", contextId, "wan2");

        (await Targets().SetWanContextAsync(targetId, null)).Should().BeTrue();

        var row = await ReadAsync(targetId);
        row.WanContextId.Should().BeNull();
        row.WanInterface.Should().Be(MonitoringTarget.UnpinnedWan);
    }

    [Fact]
    public async Task Assigning_to_a_context_that_names_no_wan_leaves_the_stamp_empty()
    {
        // A context created before the WAN column existed has nothing to copy down.
        var contextId = await SeedContextAsync("legacy", null);
        var targetId = await SeedTargetAsync("custom-hop");

        await Targets().SetWanContextAsync(targetId, contextId);

        var row = await ReadAsync(targetId);
        row.WanContextId.Should().Be(contextId);
        row.WanInterface.Should().Be(MonitoringTarget.UnpinnedWan);
    }

    [Fact]
    public async Task A_single_wan_target_that_was_never_assigned_stays_untouched()
    {
        // A no-op assignment must write NOTHING - not the stamp, not an audit event. Seeded
        // directly rather than through the migration, so the WAN stays exactly as seeded.
        var targetId = await SeedTargetAsync("custom-hop");

        (await Targets().SetWanContextAsync(targetId, null)).Should().BeTrue();

        var row = await ReadAsync(targetId);
        row.WanContextId.Should().BeNull();
        row.WanInterface.Should().BeNull();
        _audit.Drain().Suppressed.Should().BeTrue();
    }

    // ─── Path 2: a context re-pointed at another WAN ───

    [Fact]
    public async Task Repointing_a_context_restamps_every_target_it_owns()
    {
        var contextId = await SeedContextAsync("backup", "wan2");
        await SeedTargetAsync("hop-a", contextId, "wan2");
        await SeedTargetAsync("hop-b", contextId, "wan2");

        await using (var db = Db())
        {
            (await WanContextTargetStamping.RestampContextTargetsAsync(db, contextId, "wan3")).Should().Be(2);
            await db.SaveChangesAsync();
        }

        await using var read = Db();
        read.MonitoringTargets.Select(t => t.WanInterface).ToList().Should().Equal("wan3", "wan3");
    }

    [Fact]
    public async Task Repointing_a_context_leaves_another_contexts_targets_alone()
    {
        var backup = await SeedContextAsync("backup", "wan2");
        var lte = await SeedContextAsync("lte", "wan3");
        await SeedTargetAsync("hop-a", backup, "wan2");
        await SeedTargetAsync("hop-b", lte, "wan3");
        await SeedTargetAsync("hop-primary", contextId: null, wanInterface: "wan");

        await using (var db = Db())
        {
            await WanContextTargetStamping.RestampContextTargetsAsync(db, backup, "wan4");
            await db.SaveChangesAsync();
        }

        await using var read = Db();
        (await read.MonitoringTargets.SingleAsync(t => t.TargetId == "hop-a")).WanInterface.Should().Be("wan4");
        (await read.MonitoringTargets.SingleAsync(t => t.TargetId == "hop-b")).WanInterface.Should().Be("wan3");
        (await read.MonitoringTargets.SingleAsync(t => t.TargetId == "hop-primary")).WanInterface.Should().Be("wan");
    }

    // ─── Path 3: deleting a context ───

    [Fact]
    public async Task Deleting_a_context_releases_both_keys_on_its_targets()
    {
        var contextId = await SeedContextAsync("backup", "wan2");
        await SeedTargetAsync("hop-a", contextId, "wan2");

        await using (var db = Db())
        {
            (await WanContextTargetStamping.ReleaseContextTargetsAsync(db, contextId)).Should().Be(1);
            await db.SaveChangesAsync();
        }

        await using var read = Db();
        var row = await read.MonitoringTargets.SingleAsync();
        row.WanContextId.Should().BeNull();
        row.WanInterface.Should().Be(MonitoringTarget.UnpinnedWan);
    }

    [Fact]
    public async Task Deleting_a_context_touches_nothing_on_a_site_that_has_no_targets_on_it()
    {
        await SeedTargetAsync("hop-primary", contextId: null, wanInterface: "wan");

        await using (var db = Db())
        {
            (await WanContextTargetStamping.ReleaseContextTargetsAsync(db, 404)).Should().Be(0);
            await db.SaveChangesAsync();
        }

        await using var read = Db();
        (await read.MonitoringTargets.SingleAsync()).WanInterface.Should().Be("wan");
    }

    // ─── The rule itself ───

    [Fact]
    public void ApplyAssignment_carries_the_contexts_wan_and_marks_it_unpinned_on_the_way_back()
    {
        var target = new MonitoringTarget { TargetId = "t", Name = "t", Address = "203.0.113.10" };

        WanContextTargetStamping.ApplyAssignment(target, 7, "wan2");
        target.WanContextId.Should().Be(7);
        target.WanInterface.Should().Be("wan2");

        // The context id clears, but the WAN does not go blank: unpinned is a claim, and on a
        // load-balancing site it is the only true one.
        WanContextTargetStamping.ApplyAssignment(target, null, "wan2");
        target.WanContextId.Should().BeNull();
        target.WanInterface.Should().Be(MonitoringTarget.UnpinnedWan);
        MonitoringTarget.IsUnpinned(target.WanInterface).Should().BeTrue();
    }

    /// <summary>Rows predating the marker carry NULL and must read the same, or they vanish from every per-WAN view.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("unpinned", true)]
    [InlineData("UNPINNED", true)]
    [InlineData("wan", false)]
    [InlineData("wan2", false)]
    public void IsUnpinned_reads_the_legacy_null_and_the_sentinel_the_same_way(string? value, bool expected)
    {
        MonitoringTarget.IsUnpinned(value).Should().Be(expected);
    }
}

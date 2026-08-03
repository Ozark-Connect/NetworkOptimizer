using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// A monitoring target carries two WAN keys: WanContextId (who probes it, assigned by hand) and
/// WanInterface (which WAN its data describes, written by upstream discovery). Contexts predate
/// the WAN column on WanContext, so a target assigned to a secondary WAN's context has been
/// carrying no WAN at all, or the primary's - which puts its data under the wrong WAN for every
/// per-WAN reader. The BackfillWanContextTargetWan migration reconciles the two against a real
/// SQLite database, through the real migration pipeline.
/// </summary>
public class WanContextTargetWanBackfillTests : IDisposable
{
    // The migration applied immediately before the backfill.
    private const string PreBackfillMigration = "20260803193154_AddWanContextInterfaceBinding";

    private readonly string _dbPath;

    public WanContextTargetWanBackfillTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"no-wan-backfill-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private NetworkOptimizerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"DataSource={_dbPath}")
            .Options;
        return new NetworkOptimizerDbContext(options);
    }

    private static MonitoringTarget Target(string targetId, string address, int? contextId, string? wanInterface) => new()
    {
        TargetId = targetId,
        Name = targetId,
        Address = address,
        TargetType = MonitoringTargetType.Custom,
        ProbeMode = ProbeMode.Icmp,
        WanContextId = contextId,
        WanInterface = wanInterface,
    };

    [Fact]
    public void Backfill_GivesAContextsTargetsTheContextsWan()
    {
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreBackfillMigration);

            context.WanContexts.Add(new WanContext { Id = 1, Name = "backup-wan", WanInterface = "wan2", ProbeSourceIp = "198.51.100.7" });
            // Assigned to the context but never given a WAN, and assigned but stamped with the
            // primary's WAN by a discovery that predates per-WAN contexts.
            context.MonitoringTargets.Add(Target("t-unstamped", "203.0.113.1", contextId: 1, wanInterface: null));
            context.MonitoringTargets.Add(Target("t-wrong-wan", "203.0.113.2", contextId: 1, wanInterface: "wan"));
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.MonitoringTargets.OrderBy(t => t.TargetId)
                .Select(t => t.WanInterface).ToList()
                .Should().Equal("wan2", "wan2");
        }
    }

    [Fact]
    public void Backfill_LeavesTargetsWithNoContextAlone()
    {
        // Every target on a single-WAN install: no context, so nothing to reconcile against.
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreBackfillMigration);

            context.MonitoringTargets.Add(Target("t-primary", "203.0.113.3", contextId: null, wanInterface: "wan"));
            context.MonitoringTargets.Add(Target("t-manual", "203.0.113.4", contextId: null, wanInterface: null));
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.MonitoringTargets.Single(t => t.TargetId == "t-primary").WanInterface.Should().Be("wan");
            context.MonitoringTargets.Single(t => t.TargetId == "t-manual").WanInterface.Should().BeNull();
        }
    }

    [Fact]
    public void Backfill_LeavesTargetsOfAContextThatNamesNoWanAlone()
    {
        // A context created before the WAN column exists has nothing to copy down.
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreBackfillMigration);

            context.WanContexts.Add(new WanContext { Id = 2, Name = "legacy", ProbeSourceIp = "198.51.100.8" });
            context.MonitoringTargets.Add(Target("t-legacy", "203.0.113.5", contextId: 2, wanInterface: "wan"));
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.MonitoringTargets.Single().WanInterface.Should().Be("wan");
        }
    }

    [Fact]
    public void Backfill_OnACleanDatabaseDoesNothingAndDoesNotThrow()
    {
        using var context = CreateContext();

        var act = () => MigrationSafety.MigrateWithFriendlyErrors(context);

        act.Should().NotThrow();
        context.Database.GetPendingMigrations().Should().BeEmpty();
        context.MonitoringTargets.Should().BeEmpty();
    }
}

using FluentAssertions;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Seeds through raw SQL rather than the entity, naming only the columns that exist at the
    /// migration under test. Inserting via EF would use the CURRENT model, so every column added
    /// to MonitoringTargets afterwards would break these - which is the opposite of what a
    /// migration test should be sensitive to.
    /// </summary>
    private static void SeedTarget(NetworkOptimizerDbContext context, string targetId, string address,
        int? contextId, string? wanInterface) =>
        context.Database.ExecuteSqlRaw(
            "INSERT INTO MonitoringTargets (TargetId, Name, Address, TargetType, ProbeMode, "
            + "WanContextId, WanInterface, VantagePoint, PollIntervalSeconds, PingCount, Enabled, "
            + "AutoDiscovered, CreatedAt) VALUES (@id, @id, @addr, 4, 0, @ctx, @wan, 'server', 10, 5, "
            + "1, 0, datetime('now'))",
            new SqliteParameter("@id", targetId),
            new SqliteParameter("@addr", address),
            new SqliteParameter("@ctx", (object?)contextId ?? DBNull.Value),
            new SqliteParameter("@wan", (object?)wanInterface ?? DBNull.Value));



    [Fact]
    public void Backfill_GivesAContextsTargetsTheContextsWan()
    {
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreBackfillMigration);

            context.WanContexts.Add(new WanContext { Id = 1, Name = "backup-wan", WanInterface = "wan2", ProbeSourceIp = "198.51.100.7" });
            // Assigned to the context but never given a WAN, and assigned but stamped with the
            // primary's WAN by a discovery that predates per-WAN contexts.
            SeedTarget(context, "t-unstamped", "203.0.113.1", contextId: 1, wanInterface: null);
            SeedTarget(context, "t-wrong-wan", "203.0.113.2", contextId: 1, wanInterface: "wan");
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

            SeedTarget(context, "t-primary", "203.0.113.3", contextId: null, wanInterface: "wan");
            SeedTarget(context, "t-manual", "203.0.113.4", contextId: null, wanInterface: null);
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.MonitoringTargets.Single(t => t.TargetId == "t-primary").WanInterface.Should().Be("wan");
            // Still unreconciled, but no longer NULL: a later migration names the state.
            context.MonitoringTargets.Single(t => t.TargetId == "t-manual").WanInterface
                .Should().Be(MonitoringTarget.UnpinnedWan);
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
            SeedTarget(context, "t-legacy", "203.0.113.5", contextId: 2, wanInterface: "wan");
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

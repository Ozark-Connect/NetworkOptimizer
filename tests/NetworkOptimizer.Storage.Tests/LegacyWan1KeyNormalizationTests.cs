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
/// Migration 20260521500000 stamped the rows it found 'wan1'; every writer since uses 'wan'. The
/// two spell the same WAN, and nothing minded until per-WAN reading arrived - at which point a
/// 'wan' discovery run stops recognizing 'wan1' rows as its own and duplicates them, and the 'wan'
/// report stops counting them. NormalizeLegacyWan1Key folds the legacy spelling into the current
/// one, here against a real SQLite database through the real migration pipeline.
/// </summary>
public class LegacyWan1KeyNormalizationTests : IDisposable
{
    // The migration applied immediately before the normalization.
    private const string PreNormalizeMigration = "20260803210000_BackfillWanContextTargetWan";

    private readonly string _dbPath;

    public LegacyWan1KeyNormalizationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"no-wan1-normalize-test-{Guid.NewGuid():N}.db");
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
    /// Seeds through raw SQL rather than the entity - see the note in
    /// WanContextTargetWanBackfillTests: an EF insert would use the current model and break on
    /// every column added to MonitoringTargets after this migration.
    /// </summary>
    private static void SeedTarget(NetworkOptimizerDbContext context, string targetId, string? wanInterface) =>
        context.Database.ExecuteSqlRaw(
            "INSERT INTO MonitoringTargets (TargetId, Name, Address, TargetType, ProbeMode, "
            + "WanInterface, VantagePoint, PollIntervalSeconds, PingCount, Enabled, AutoDiscovered, "
            + "CreatedAt) VALUES (@id, @id, '203.0.113.10', 2, 0, @wan, 'server', 10, 5, 1, 0, "
            + "datetime('now'))",
            new SqliteParameter("@id", targetId),
            new SqliteParameter("@wan", (object?)wanInterface ?? DBNull.Value));



    [Fact]
    public void Normalize_RenamesTheLegacySpellingEverywhereItIsStored()
    {
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreNormalizeMigration);

            context.WanDiscoveryContexts.Add(new WanDiscoveryContext { WanInterface = "wan1", AccessTechnology = AccessTechnology.Gpon });
            SeedTarget(context, "access-legacy", "wan1");
            context.UpstreamDiscoveries.Add(new UpstreamDiscovery { HopIp = "192.0.2.30", HopNumber = 1, WanInterface = "wan1" });
            context.WanContexts.Add(new WanContext { Id = 1, Name = "legacy-context", WanInterface = "wan1", ProbeSourceIp = "198.51.100.9" });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.WanDiscoveryContexts.Single().WanInterface.Should().Be("wan");
            context.WanDiscoveryContexts.Single().AccessTechnology.Should().Be(AccessTechnology.Gpon);
            context.MonitoringTargets.Single().WanInterface.Should().Be("wan");
            context.UpstreamDiscoveries.Single().WanInterface.Should().Be("wan");
            context.WanContexts.Single().WanInterface.Should().Be("wan");
        }
    }

    [Fact]
    public void Normalize_KeepsTheNewerDiscoveryContextWhenBothSpellingsExist()
    {
        // WanDiscoveryContexts is keyed by the WAN, so the two rows cannot both survive the
        // rename. The row describing the more recent discovery is the one worth keeping.
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreNormalizeMigration);

            context.WanDiscoveryContexts.Add(new WanDiscoveryContext
            {
                WanInterface = "wan1",
                AccessTechnology = AccessTechnology.Docsis,
                LastDiscoveryAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            context.WanDiscoveryContexts.Add(new WanDiscoveryContext
            {
                WanInterface = "wan",
                AccessTechnology = AccessTechnology.XgsPon,
                LastDiscoveryAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.WanDiscoveryContexts.Single().Should().Match<WanDiscoveryContext>(
                c => c.WanInterface == "wan" && c.AccessTechnology == AccessTechnology.XgsPon);
        }
    }

    [Fact]
    public void Normalize_KeepsTheLegacyRowWhenItIsTheNewerOne()
    {
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreNormalizeMigration);

            context.WanDiscoveryContexts.Add(new WanDiscoveryContext
            {
                WanInterface = "wan1",
                AccessTechnology = AccessTechnology.XgsPon,
                LastDiscoveryAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            context.WanDiscoveryContexts.Add(new WanDiscoveryContext
            {
                WanInterface = "wan",
                AccessTechnology = AccessTechnology.Docsis,
                LastDiscoveryAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.WanDiscoveryContexts.Single().Should().Match<WanDiscoveryContext>(
                c => c.WanInterface == "wan" && c.AccessTechnology == AccessTechnology.XgsPon);
        }
    }

    [Fact]
    public void Normalize_LeavesEveryOtherWanAlone()
    {
        using (var context = CreateContext())
        {
            context.GetService<IMigrator>().Migrate(PreNormalizeMigration);

            context.WanDiscoveryContexts.Add(new WanDiscoveryContext { WanInterface = "wan2" });
            SeedTarget(context, "access-wan2", "wan2");
            SeedTarget(context, "access-unstamped", null);
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            MigrationSafety.MigrateWithFriendlyErrors(context);

            context.WanDiscoveryContexts.Single().WanInterface.Should().Be("wan2");
            context.MonitoringTargets.Single(t => t.TargetId == "access-wan2").WanInterface.Should().Be("wan2");
            // Normalization leaves it alone; the later unpinned migration then names it.
            context.MonitoringTargets.Single(t => t.TargetId == "access-unstamped").WanInterface
                .Should().Be(MonitoringTarget.UnpinnedWan);
        }
    }

    [Fact]
    public void Normalize_OnACleanDatabaseDoesNothingAndDoesNotThrow()
    {
        using var context = CreateContext();

        var act = () => MigrationSafety.MigrateWithFriendlyErrors(context);

        act.Should().NotThrow();
        context.Database.GetPendingMigrations().Should().BeEmpty();
        context.WanDiscoveryContexts.Should().BeEmpty();
    }
}

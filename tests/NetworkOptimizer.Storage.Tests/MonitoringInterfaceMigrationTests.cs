using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Exercises the real EF Core migration pipeline against a file-backed SQLite database (no
/// EnsureCreated, no EF InMemory provider). Covers the AddMonitoringInterfaceAlias migration that
/// tightens the WanIfName+Name unique index down to Name alone, and the MigrationSafety wrapper that
/// turns the resulting raw SQLite constraint error into an actionable message.
/// </summary>
public class MonitoringInterfaceMigrationTests : IDisposable
{
    // The migration applied immediately before AddMonitoringInterfaceAlias.
    private const string PreAliasMigration = "20260702170555_AddNeighborSightingCount";

    private readonly string _dbPath;

    public MonitoringInterfaceMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"no-migration-test-{Guid.NewGuid():N}.db");
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

    [Fact]
    public void MigrateWithFriendlyErrors_OnCleanDatabase_AppliesAllMigrationsWithoutThrowing()
    {
        // The common path: a brand-new install migrates from empty to current. This is the
        // regression guard for the old invalid RAISE() SQL that used to make every startup throw.
        using var context = CreateContext();

        var act = () => MigrationSafety.MigrateWithFriendlyErrors(context);

        act.Should().NotThrow();
        context.Database.GetPendingMigrations().Should().BeEmpty();
        context.MonitoringInterfaces.Should().BeEmpty();
    }

    [Fact]
    public void MigrateWithFriendlyErrors_WhenExistingRowsShareNameAcrossWans_ThrowsHelpfulError()
    {
        // Simulate an existing database from before the tightening: migrate up to (but not
        // including) AddMonitoringInterfaceAlias, then seed two rows that share a Name on
        // different WANs - legal under the old WanIfName+Name index but not the new Name index.
        using (var context = CreateContext())
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate(PreAliasMigration);

            context.Database.ExecuteSqlRaw(
                """
                INSERT INTO MonitoringInterfaces
                    (Name, WanIfName, TargetIp, SubnetPrefix, GatewayLocalIp, SnatEnabled, WatchdogIntervalMinutes, IsManuallyDeployed, CreatedAt, UpdatedAt)
                VALUES
                    ('modem0', 'eth4', '192.168.100.1', 24, '192.168.100.2', 1, 5, 0, '2026-01-01T00:00:00', '2026-01-01T00:00:00'),
                    ('modem0', 'eth2', '192.168.200.1', 24, '192.168.200.2', 1, 5, 0, '2026-01-01T00:00:00', '2026-01-01T00:00:00');
                """);
        }

        using (var context = CreateContext())
        {
            var act = () => MigrationSafety.MigrateWithFriendlyErrors(context);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*same Name*")
                .WithMessage("*rename*");
        }
    }

    [Fact]
    public void MigrateWithFriendlyErrors_WhenExistingRowsShareGatewayLocalIpAcrossWans_ThrowsHelpfulError()
    {
        // GatewayLocalIp was never unique before this migration - a dirty pre-existing database
        // can genuinely have two interfaces sharing it (e.g. copy-pasted config on different
        // WANs). Names differ so only the GatewayLocalIp index is exercised.
        using (var context = CreateContext())
        {
            var migrator = context.GetService<IMigrator>();
            migrator.Migrate(PreAliasMigration);

            context.Database.ExecuteSqlRaw(
                """
                INSERT INTO MonitoringInterfaces
                    (Name, WanIfName, TargetIp, SubnetPrefix, GatewayLocalIp, SnatEnabled, WatchdogIntervalMinutes, IsManuallyDeployed, CreatedAt, UpdatedAt)
                VALUES
                    ('modem0', 'eth4', '192.168.100.1', 24, '192.168.100.2', 1, 5, 0, '2026-01-01T00:00:00', '2026-01-01T00:00:00'),
                    ('modem1', 'eth2', '192.168.200.1', 24, '192.168.100.2', 1, 5, 0, '2026-01-01T00:00:00', '2026-01-01T00:00:00');
                """);
        }

        using (var context = CreateContext())
        {
            var act = () => MigrationSafety.MigrateWithFriendlyErrors(context);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*same GatewayLocalIp*")
                .WithMessage("*change*");
        }
    }
}

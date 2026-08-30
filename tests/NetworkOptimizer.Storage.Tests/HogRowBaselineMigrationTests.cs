using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Exercises the AddHogRowBaselines migration against a file-backed SQLite database, and - the
/// part no other test covers - asserts the hand-authored model snapshot still matches the
/// compiled model, which is the mistake hand-editing the snapshot invites.
/// </summary>
public class HogRowBaselineMigrationTests : IDisposable
{
    private readonly string _dbPath;

    public HogRowBaselineMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"no-hog-baseline-test-{Guid.NewGuid():N}.db");
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

    private NetworkOptimizerDbContext CreateMigratedContext()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"DataSource={_dbPath}")
            .Options;
        var context = new NetworkOptimizerDbContext(options);
        MigrationSafety.MigrateWithFriendlyErrors(context);
        return context;
    }

    [Fact]
    public void EveryMigrationApplies_AndTheSnapshotMatchesTheModel()
    {
        using var context = CreateMigratedContext();

        context.Database.GetPendingMigrations().Should().BeEmpty();
        context.Database.HasPendingModelChanges().Should().BeFalse(
            "the snapshot is maintained by hand, and drift from the compiled model breaks the next migration");
    }

    [Fact]
    public async Task Baselines_RoundtripByRowKey()
    {
        var at = new DateTime(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);
        using (var context = CreateMigratedContext())
        {
            context.HogRowBaselines.Add(new HogRowBaseline { RowKey = "port:aa:bb:cc:dd:ee:01|eth1", DownBps = 30e6, UpBps = 1e6, UpdatedAt = at });
            context.HogRowBaselines.Add(new HogRowBaseline { RowKey = "wifi:aa:bb:cc:dd:ee:02", DownBps = 5e6, UpBps = 0, UpdatedAt = at });
            await context.SaveChangesAsync();
        }

        using (var context = CreateMigratedContext())
        {
            var port = await context.HogRowBaselines.SingleAsync(b => b.RowKey == "port:aa:bb:cc:dd:ee:01|eth1");
            port.DownBps.Should().Be(30e6);
            port.UpBps.Should().Be(1e6);
            port.UpdatedAt.Should().Be(at);
            (await context.HogRowBaselines.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task ASecondRowForTheSameKey_IsRejected()
    {
        using (var context = CreateMigratedContext())
        {
            context.HogRowBaselines.Add(new HogRowBaseline { RowKey = "wifi:aa:bb:cc:dd:ee:03", DownBps = 1 });
            await context.SaveChangesAsync();
        }

        // A fresh context, so the database enforces the key rather than EF's change tracker.
        using var second = CreateMigratedContext();
        second.HogRowBaselines.Add(new HogRowBaseline { RowKey = "wifi:aa:bb:cc:dd:ee:03", DownBps = 2 });
        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("RowKey is the table's key");
    }
}

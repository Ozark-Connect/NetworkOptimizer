using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>The AddLanHealthClientMac migration against a file-backed SQLite database: the column applies and round-trips.</summary>
public class LanHealthClientMacMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"no-lanh-clientmac-test-{Guid.NewGuid():N}.db");

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
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite($"DataSource={_dbPath}").Options;
        var context = new NetworkOptimizerDbContext(options);
        MigrationSafety.MigrateWithFriendlyErrors(context);
        return context;
    }

    [Fact]
    public async Task ClientMac_RoundTrips_AndStaysNullForFabricRows()
    {
        var hour = new DateTime(2026, 11, 26, 4, 0, 0, DateTimeKind.Utc);
        using (var context = CreateMigratedContext())
        {
            context.Database.GetPendingMigrations().Should().BeEmpty();
            context.LanHealthLinkHours.Add(new LanHealthLinkHour { HourStart = hour, LinkId = "sw|5-access", ClientMac = "00:11:22:33:44:02" });
            context.LanHealthLinkHours.Add(new LanHealthLinkHour { HourStart = hour, LinkId = "gw|3-ap|1" });
            await context.SaveChangesAsync();
        }

        using (var context = CreateMigratedContext())
        {
            (await context.LanHealthLinkHours.SingleAsync(r => r.LinkId == "sw|5-access")).ClientMac.Should().Be("00:11:22:33:44:02");
            (await context.LanHealthLinkHours.SingleAsync(r => r.LinkId == "gw|3-ap|1")).ClientMac.Should().BeNull();
        }
    }
}

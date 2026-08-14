using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Exercises the AddFirmwareRollout migration against a file-backed SQLite database, so the
/// constraints the EF InMemory provider ignores (unique model index, plan-to-step cascade)
/// are actually proven.
/// </summary>
public class FirmwareRolloutMigrationTests : IDisposable
{
    private readonly string _dbPath;

    public FirmwareRolloutMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"no-firmware-rollout-test-{Guid.NewGuid():N}.db");
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

    private static FirmwareRolloutRepository RepositoryFor(NetworkOptimizerDbContext context) =>
        new(context, new Mock<ILogger<FirmwareRolloutRepository>>().Object);

    [Fact]
    public async Task Migration_CreatesTheRolloutTables()
    {
        using var context = CreateMigratedContext();

        context.Database.GetPendingMigrations().Should().BeEmpty();
        (await context.FirmwareRolloutSettings.CountAsync()).Should().Be(0);
        (await context.FirmwareRolloutPlans.CountAsync()).Should().Be(0);
        (await context.FirmwareRolloutSteps.CountAsync()).Should().Be(0);
        (await context.FirmwareModelTimings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletingAPlan_CascadesToItsSteps()
    {
        using var context = CreateMigratedContext();
        var repository = RepositoryFor(context);

        var plan = await repository.CreatePlanAsync(new FirmwareRolloutPlan { CreatedBy = "Admin" });
        await repository.AddStepsAsync(
        [
            new FirmwareRolloutStep
            {
                PlanId = plan.Id, DeviceMac = "aa:bb:cc:dd:ee:ff", DeviceName = "Access Point 1",
                Model = "U6-Lite", DeviceType = "uap", Channel = "release", Wave = 1,
            },
        ]);

        context.FirmwareRolloutPlans.Remove(await context.FirmwareRolloutPlans.SingleAsync(p => p.Id == plan.Id));
        await context.SaveChangesAsync();

        (await context.FirmwareRolloutSteps.CountAsync()).Should().Be(0, "steps belong to their plan");
    }

    [Fact]
    public async Task ModelTimings_RejectASecondRowForTheSameModel()
    {
        using var context = CreateMigratedContext();

        context.FirmwareModelTimings.Add(new FirmwareModelTiming { Model = "U7-Pro" });
        await context.SaveChangesAsync();
        context.FirmwareModelTimings.Add(new FirmwareModelTiming { Model = "U7-Pro" });

        var act = async () => await context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>("Model is the timing store's key");
    }

    [Fact]
    public async Task SettingsRoundtrip_SurvivesTheEnumToIntegerMapping()
    {
        using var context = CreateMigratedContext();
        var repository = RepositoryFor(context);

        await repository.SaveSettingsAsync(new FirmwareRolloutSettings
        {
            Mode = FirmwareRolloutMode.Autopilot,
            SpacingProfile = FirmwareSpacingProfile.Conservative,
            AutopilotWindowMode = FirmwareAutopilotWindowMode.Fixed,
            FixedDayOfWeek = 0,
            FixedHour = 3,
        });

        var saved = await repository.GetSettingsAsync();
        saved.Mode.Should().Be(FirmwareRolloutMode.Autopilot);
        saved.SpacingProfile.Should().Be(FirmwareSpacingProfile.Conservative);
        saved.AutopilotWindowMode.Should().Be(FirmwareAutopilotWindowMode.Fixed);
        saved.FixedDayOfWeek.Should().Be(0);
        saved.FixedHour.Should().Be(3);
    }

    [Fact]
    public async Task ActivePlanQuery_TranslatesTheTerminalStatusFilterOnSqlite()
    {
        using var context = CreateMigratedContext();
        var repository = RepositoryFor(context);

        await repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = FirmwareRolloutStatus.Reported, CreatedBy = "Admin" });
        var running = await repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = FirmwareRolloutStatus.Running, CreatedBy = "Admin" });

        var active = await repository.GetActivePlanAsync();

        active!.Id.Should().Be(running.Id);
    }
}

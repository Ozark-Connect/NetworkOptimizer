using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class FirmwareRolloutRepositoryTests : IDisposable
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly NetworkOptimizerDbContext _context;
    private readonly FirmwareRolloutRepository _repository;

    public FirmwareRolloutRepositoryTests()
    {
        _context = NewContext();
        var logger = new Mock<ILogger<FirmwareRolloutRepository>>();
        _repository = new FirmwareRolloutRepository(_context, logger.Object);
    }

    public void Dispose() => _context.Dispose();

    /// <summary>A second context over the same store, so assertions read persisted state.</summary>
    private NetworkOptimizerDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;
        return new NetworkOptimizerDbContext(options);
    }

    #region Settings

    [Fact]
    public async Task GetSettingsAsync_NoRow_CreatesAndPersistsDefaults()
    {
        var settings = await _repository.GetSettingsAsync();

        settings.Mode.Should().Be(FirmwareRolloutMode.ManualOnly);
        settings.GlobalChannel.Should().Be("release");
        settings.SuppressStandardAlerts.Should().BeTrue();
        settings.NotifyHoursAhead.Should().Be(12);
        settings.SoakHours.Should().Be(24);
        settings.MinReleaseAgeDays.Should().Be(0);
        settings.WaiveBackup.Should().BeFalse();
        settings.PerWaveApproval.Should().BeFalse();
        settings.AutopilotWindowMode.Should().Be(FirmwareAutopilotWindowMode.Auto);

        // Null on both console channels is the default: they follow the global channel rather
        // than being pinned to one of their own.
        settings.NetworkAppChannel.Should().BeNull();
        settings.UniFiOsChannel.Should().BeNull();
        settings.EffectiveNetworkAppChannel.Should().Be("release");
        settings.EffectiveUniFiOsChannel.Should().Be("release");

        using var verify = NewContext();
        (await verify.FirmwareRolloutSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetSettingsAsync_CalledTwice_DoesNotCreateASecondRow()
    {
        await _repository.GetSettingsAsync();
        await _repository.GetSettingsAsync();

        using var verify = NewContext();
        (await verify.FirmwareRolloutSettings.CountAsync()).Should().Be(1, "the settings row is a singleton per site");
    }

    [Fact]
    public async Task SaveSettingsAsync_UpdatePath_CopiesEveryField()
    {
        // The update path assigns field by field, so a new column silently keeps its old
        // value unless it is added there. This asserts the whole surface survives a save.
        await _repository.GetSettingsAsync();

        var incoming = new FirmwareRolloutSettings
        {
            Mode = FirmwareRolloutMode.Autopilot,
            GlobalChannel = "release-candidate",
            PerDeviceTypeChannelsJson = """{"uap":"beta"}""",
            PerSkuChannelsJson = """{"U7-Pro":"release"}""",
            NetworkAppChannel = "beta",
            UniFiOsChannel = "release",
            IncludeUniFiOs = false,
            IncludeUniFiNetwork = false,
            ExclusionsJson = """{"macs":["aa:bb:cc:dd:ee:ff"],"skus":["USW-16-PoE"],"deviceTypes":["ugw"]}""",
            SpacingProfile = FirmwareSpacingProfile.Fast,
            AdvancedSpacingJson = """{"apSeconds":120,"maxApParallelism":3}""",
            SuppressStandardAlerts = false,
            AutopilotWindowMode = FirmwareAutopilotWindowMode.Fixed,
            FixedDayOfWeek = 6,
            FixedHour = 3,
            NotifyHoursAhead = 24,
            SoakHours = 48,
            MinReleaseAgeDays = 7,
            WaiveBackup = true,
            PerWaveApproval = true,
        };

        await _repository.SaveSettingsAsync(incoming);

        using var verify = NewContext();
        var saved = await verify.FirmwareRolloutSettings.AsNoTracking().SingleAsync();
        saved.Mode.Should().Be(FirmwareRolloutMode.Autopilot);
        saved.GlobalChannel.Should().Be("release-candidate");
        saved.PerDeviceTypeChannelsJson.Should().Be("""{"uap":"beta"}""");
        saved.PerSkuChannelsJson.Should().Be("""{"U7-Pro":"release"}""");
        saved.NetworkAppChannel.Should().Be("beta");
        saved.UniFiOsChannel.Should().Be("release");
        saved.IncludeUniFiOs.Should().BeFalse();
        saved.IncludeUniFiNetwork.Should().BeFalse();
        saved.ExclusionsJson.Should().Contain("aa:bb:cc:dd:ee:ff");
        saved.SpacingProfile.Should().Be(FirmwareSpacingProfile.Fast);
        saved.AdvancedSpacingJson.Should().Be("""{"apSeconds":120,"maxApParallelism":3}""");
        saved.SuppressStandardAlerts.Should().BeFalse();
        saved.AutopilotWindowMode.Should().Be(FirmwareAutopilotWindowMode.Fixed);
        saved.FixedDayOfWeek.Should().Be(6);
        saved.FixedHour.Should().Be(3);
        saved.NotifyHoursAhead.Should().Be(24);
        saved.SoakHours.Should().Be(48);
        saved.MinReleaseAgeDays.Should().Be(7);
        saved.WaiveBackup.Should().BeTrue();
        saved.PerWaveApproval.Should().BeTrue();
        saved.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task SaveSettingsAsync_ClearingAConsoleChannel_PutsItBackOnTheGlobalChannel()
    {
        await _repository.SaveSettingsAsync(new FirmwareRolloutSettings
        {
            GlobalChannel = "release",
            NetworkAppChannel = "beta",
            UniFiOsChannel = "beta",
        });

        await _repository.SaveSettingsAsync(new FirmwareRolloutSettings
        {
            GlobalChannel = "release-candidate",
            NetworkAppChannel = null,
            UniFiOsChannel = null,
        });

        using var verify = NewContext();
        var saved = await verify.FirmwareRolloutSettings.AsNoTracking().SingleAsync();
        saved.NetworkAppChannel.Should().BeNull();
        saved.UniFiOsChannel.Should().BeNull();
        saved.EffectiveNetworkAppChannel.Should().Be("release-candidate");
        saved.EffectiveUniFiOsChannel.Should().Be("release-candidate");
    }

    [Fact]
    public void EffectiveConsoleChannels_PreferTheOverrideOverTheGlobalChannel()
    {
        var settings = new FirmwareRolloutSettings
        {
            GlobalChannel = "release",
            NetworkAppChannel = "beta",
            UniFiOsChannel = "release-candidate",
        };

        settings.EffectiveNetworkAppChannel.Should().Be("beta");
        settings.EffectiveUniFiOsChannel.Should().Be("release-candidate");
    }

    [Fact]
    public async Task SaveSettingsAsync_InsertPath_StoresTheRowAndStampsUpdatedAt()
    {
        await _repository.SaveSettingsAsync(new FirmwareRolloutSettings
        {
            Mode = FirmwareRolloutMode.Off,
            GlobalChannel = "beta",
            UpdatedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        using var verify = NewContext();
        var saved = await verify.FirmwareRolloutSettings.AsNoTracking().SingleAsync();
        saved.Mode.Should().Be(FirmwareRolloutMode.Off);
        saved.GlobalChannel.Should().Be("beta");
        saved.UpdatedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    #endregion

    #region Plans

    [Fact]
    public async Task PlanLifecycle_EveryStatusTransitionIsPersisted()
    {
        var plan = await _repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.Draft,
            PlanJson = """{"waves":[]}""",
            CreatedBy = "Admin",
        });
        plan.Id.Should().BeGreaterThan(0);

        var scheduledAt = new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc);
        var transitions = new[]
        {
            FirmwareRolloutStatus.Scheduled,
            FirmwareRolloutStatus.Announced,
            FirmwareRolloutStatus.Running,
            FirmwareRolloutStatus.Paused,
            FirmwareRolloutStatus.Running,
            FirmwareRolloutStatus.SoakWait,
            FirmwareRolloutStatus.Reported,
        };

        foreach (var status in transitions)
        {
            plan.Status = status;
            plan.ScheduledStartAt = scheduledAt;
            await _repository.UpdatePlanAsync(plan);

            using var check = NewContext();
            var stored = await check.FirmwareRolloutPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
            stored.Status.Should().Be(status);
            stored.ScheduledStartAt.Should().Be(scheduledAt);
        }
    }

    [Fact]
    public async Task UpdatePlanAsync_CopiesMutableFieldsAndLeavesCreatedAtAlone()
    {
        var created = await _repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            PlanJson = "{}",
            CreatedBy = "Admin",
        });
        var createdAt = created.CreatedAt;
        // Edit a detached read, the way a service does - not the instance the context tracks.
        var plan = (await _repository.GetPlanAsync(created.Id))!;

        plan.Status = FirmwareRolloutStatus.Reported;
        plan.StartedAt = new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc);
        plan.CompletedAt = new DateTime(2026, 8, 15, 4, 30, 0, DateTimeKind.Utc);
        plan.PlanJson = """{"waves":[1,2]}""";
        plan.OriginalChannelSettingsJson = """{"network":"release"}""";
        plan.ReportJson = """{"devices":2}""";
        plan.CreatedBy = "autopilot";
        plan.CreatedAt = DateTime.UtcNow.AddYears(-5);

        await _repository.UpdatePlanAsync(plan);

        using var verify = NewContext();
        var saved = await verify.FirmwareRolloutPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
        saved.Status.Should().Be(FirmwareRolloutStatus.Reported);
        saved.StartedAt.Should().Be(new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc));
        saved.CompletedAt.Should().Be(new DateTime(2026, 8, 15, 4, 30, 0, DateTimeKind.Utc));
        saved.PlanJson.Should().Be("""{"waves":[1,2]}""");
        saved.OriginalChannelSettingsJson.Should().Be("""{"network":"release"}""");
        saved.ReportJson.Should().Be("""{"devices":2}""");
        saved.CreatedBy.Should().Be("autopilot");
        saved.CreatedAt.Should().Be(createdAt, "CreatedAt is history and is not rewritten by an update");
    }

    [Fact]
    public async Task UpdatePlanAsync_UnknownId_DoesNotThrow()
    {
        var act = async () => await _repository.UpdatePlanAsync(new FirmwareRolloutPlan { Id = 9999, CreatedBy = "Admin" });
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(FirmwareRolloutStatus.Draft)]
    [InlineData(FirmwareRolloutStatus.Scheduled)]
    [InlineData(FirmwareRolloutStatus.Announced)]
    [InlineData(FirmwareRolloutStatus.Running)]
    [InlineData(FirmwareRolloutStatus.Paused)]
    [InlineData(FirmwareRolloutStatus.SoakWait)]
    public async Task GetActivePlanAsync_NonTerminalStatus_IsReturned(FirmwareRolloutStatus status)
    {
        await _repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = status, CreatedBy = "Admin" });

        var active = await _repository.GetActivePlanAsync();

        active.Should().NotBeNull();
        active!.Status.Should().Be(status);
    }

    [Theory]
    [InlineData(FirmwareRolloutStatus.Reported)]
    [InlineData(FirmwareRolloutStatus.Aborted)]
    [InlineData(FirmwareRolloutStatus.Failed)]
    public async Task GetActivePlanAsync_TerminalStatus_IsExcluded(FirmwareRolloutStatus status)
    {
        await _repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = status, CreatedBy = "Admin" });

        (await _repository.GetActivePlanAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetActivePlanAsync_PastRunsPresent_ReturnsTheOneStillInFlight()
    {
        await _repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = FirmwareRolloutStatus.Reported, CreatedBy = "Admin" });
        await _repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = FirmwareRolloutStatus.Aborted, CreatedBy = "Admin" });
        var running = await _repository.CreatePlanAsync(new FirmwareRolloutPlan { Status = FirmwareRolloutStatus.Running, CreatedBy = "autopilot" });

        var active = await _repository.GetActivePlanAsync();

        active!.Id.Should().Be(running.Id);
    }

    [Fact]
    public async Task GetPlanHistoryAsync_ReturnsNewestFirstAndHonorsTheLimit()
    {
        for (var i = 0; i < 5; i++)
            await _repository.CreatePlanAsync(new FirmwareRolloutPlan { CreatedBy = $"Admin{i}" });

        var history = await _repository.GetPlanHistoryAsync(limit: 3);

        history.Should().HaveCount(3);
        history.Should().BeInDescendingOrder(p => p.Id);
        history[0].CreatedBy.Should().Be("Admin4");
    }

    [Fact]
    public async Task GetPlanAsync_UnknownId_ReturnsNull()
    {
        (await _repository.GetPlanAsync(9999)).Should().BeNull();
    }

    #endregion

    #region Steps

    [Fact]
    public async Task AddStepsAsync_ThenGetStepsAsync_ReturnsPlanStepsInWaveOrder()
    {
        var plan = await _repository.CreatePlanAsync(new FirmwareRolloutPlan { CreatedBy = "Admin" });
        var otherPlan = await _repository.CreatePlanAsync(new FirmwareRolloutPlan { CreatedBy = "Admin" });

        await _repository.AddStepsAsync(
        [
            NewStep(plan.Id, "aa:bb:cc:dd:ee:02", wave: 2),
            NewStep(plan.Id, "aa:bb:cc:dd:ee:01", wave: 1),
            NewStep(otherPlan.Id, "aa:bb:cc:dd:ee:03", wave: 1),
        ]);

        var steps = await _repository.GetStepsAsync(plan.Id);

        steps.Should().HaveCount(2);
        steps.Select(s => s.DeviceMac).Should().ContainInOrder("aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02");
    }

    [Fact]
    public async Task AddStepsAsync_EmptySequence_IsANoOp()
    {
        var act = async () => await _repository.AddStepsAsync([]);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UpdateStepAsync_CopiesEveryField()
    {
        var plan = await _repository.CreatePlanAsync(new FirmwareRolloutPlan { CreatedBy = "Admin" });
        var step = NewStep(plan.Id, "aa:bb:cc:dd:ee:ff", wave: 1);
        await _repository.AddStepsAsync([step]);

        step.DeviceName = "Access Point 2";
        step.Model = "U7-Pro";
        step.DeviceType = "uap";
        step.FromVersion = "7.0.20";
        step.ToVersion = "7.1.10";
        step.Channel = "release-candidate";
        step.Wave = 4;
        step.State = FirmwareRolloutStepState.RegressionFlagged;
        step.CommandedAt = new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc);
        step.WentDownAt = new DateTime(2026, 8, 15, 3, 1, 0, DateTimeKind.Utc);
        step.BackAt = new DateTime(2026, 8, 15, 3, 5, 0, DateTimeKind.Utc);
        step.DowntimeSeconds = 240;
        step.PreStatsJson = """{"cpu":11.0}""";
        step.PostStatsJson = """{"cpu":38.5}""";
        step.Error = "litmus flagged CPU";

        await _repository.UpdateStepAsync(step);

        using var verify = NewContext();
        var saved = await verify.FirmwareRolloutSteps.AsNoTracking().SingleAsync(s => s.Id == step.Id);
        saved.PlanId.Should().Be(plan.Id);
        saved.DeviceMac.Should().Be("aa:bb:cc:dd:ee:ff");
        saved.DeviceName.Should().Be("Access Point 2");
        saved.Model.Should().Be("U7-Pro");
        saved.DeviceType.Should().Be("uap");
        saved.FromVersion.Should().Be("7.0.20");
        saved.ToVersion.Should().Be("7.1.10");
        saved.Channel.Should().Be("release-candidate");
        saved.Wave.Should().Be(4);
        saved.State.Should().Be(FirmwareRolloutStepState.RegressionFlagged);
        saved.CommandedAt.Should().Be(new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc));
        saved.WentDownAt.Should().Be(new DateTime(2026, 8, 15, 3, 1, 0, DateTimeKind.Utc));
        saved.BackAt.Should().Be(new DateTime(2026, 8, 15, 3, 5, 0, DateTimeKind.Utc));
        saved.DowntimeSeconds.Should().Be(240);
        saved.PreStatsJson.Should().Be("""{"cpu":11.0}""");
        saved.PostStatsJson.Should().Be("""{"cpu":38.5}""");
        saved.Error.Should().Be("litmus flagged CPU");
    }

    [Fact]
    public async Task UpdateStepAsync_UnknownId_DoesNotThrow()
    {
        var act = async () => await _repository.UpdateStepAsync(NewStep(planId: 1, "aa:bb:cc:dd:ee:ff", wave: 1, id: 9999));
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region Model timing

    [Fact]
    public async Task RecordModelTimingAsync_FirstSample_SeedsMedianAndP90FromIt()
    {
        var timing = await _repository.RecordModelTimingAsync("U7-Pro", 200);

        timing.SampleCount.Should().Be(1);
        timing.MedianDowntimeSeconds.Should().Be(200);
        timing.P90DowntimeSeconds.Should().Be(200);
    }

    [Fact]
    public async Task RecordModelTimingAsync_OddSampleCount_TakesTheMiddleValue()
    {
        await _repository.RecordModelTimingAsync("USW-Pro-24", 300);
        await _repository.RecordModelTimingAsync("USW-Pro-24", 100);
        var timing = await _repository.RecordModelTimingAsync("USW-Pro-24", 200);

        timing.SampleCount.Should().Be(3);
        timing.MedianDowntimeSeconds.Should().Be(200);
        timing.P90DowntimeSeconds.Should().Be(300, "nearest-rank p90 of three samples is the slowest one");
    }

    [Fact]
    public async Task RecordModelTimingAsync_EvenSampleCount_AveragesTheTwoMiddleValues()
    {
        await _repository.RecordModelTimingAsync("U6-Lite", 180);
        await _repository.RecordModelTimingAsync("U6-Lite", 200);
        await _repository.RecordModelTimingAsync("U6-Lite", 240);
        var timing = await _repository.RecordModelTimingAsync("U6-Lite", 300);

        timing.MedianDowntimeSeconds.Should().Be(220);
        timing.P90DowntimeSeconds.Should().Be(300);
    }

    [Fact]
    public async Task RecordModelTimingAsync_OutlierSample_MovesP90WithoutDraggingTheMedian()
    {
        foreach (var sample in new[] { 200, 210, 220, 230, 240, 250, 260, 270, 280 })
            await _repository.RecordModelTimingAsync("U7-Pro", sample);

        var timing = await _repository.RecordModelTimingAsync("U7-Pro", 900);

        timing.SampleCount.Should().Be(10);
        timing.MedianDowntimeSeconds.Should().Be(245);
        timing.P90DowntimeSeconds.Should().Be(280);
    }

    [Fact]
    public async Task RecordModelTimingAsync_KeepsCountingBeyondTheRetainedWindow()
    {
        for (var i = 0; i < 55; i++)
            await _repository.RecordModelTimingAsync("USW-Lite-8-PoE", 400);
        var timing = await _repository.RecordModelTimingAsync("USW-Lite-8-PoE", 500);

        timing.SampleCount.Should().Be(56, "SampleCount is the lifetime count, not the window size");
        timing.MedianDowntimeSeconds.Should().Be(400);
        timing.P90DowntimeSeconds.Should().Be(400, "one late sample in a full window does not move the 90th percentile");
    }

    [Fact]
    public async Task RecordModelTimingAsync_UpsertsByModelAndKeepsModelsApart()
    {
        await _repository.RecordModelTimingAsync("U7-Pro", 200);
        await _repository.RecordModelTimingAsync("U7-Pro", 220);
        await _repository.RecordModelTimingAsync("USW-Pro-24", 480);

        using var verify = NewContext();
        (await verify.FirmwareModelTimings.CountAsync()).Should().Be(2, "one row per model");

        var timings = await _repository.GetModelTimingsAsync();
        timings.Select(t => t.Model).Should().ContainInOrder("U7-Pro", "USW-Pro-24");
        timings.Single(t => t.Model == "U7-Pro").SampleCount.Should().Be(2);
        timings.Single(t => t.Model == "USW-Pro-24").MedianDowntimeSeconds.Should().Be(480);
    }

    [Fact]
    public async Task GetModelTimingAsync_UnseenModel_ReturnsNull()
    {
        (await _repository.GetModelTimingAsync("UX7")).Should().BeNull();
    }

    [Fact]
    public async Task RecordModelTimingAsync_BlankModel_Throws()
    {
        var act = async () => await _repository.RecordModelTimingAsync(" ", 200);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    #endregion

    private static FirmwareRolloutStep NewStep(int planId, string mac, int wave, int id = 0) => new()
    {
        Id = id,
        PlanId = planId,
        DeviceMac = mac,
        DeviceName = "Access Point 1",
        Model = "U6-Lite",
        DeviceType = "uap",
        Channel = "release",
        Wave = wave,
    };
}

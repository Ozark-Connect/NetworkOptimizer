using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The gated service is what the wizard and the live view call, so these cover the contract it
/// offers them: a preview that changes nothing, a commit that persists the plan AND the settings it
/// was planned from, and controls that reach the site's executor and refuse a stale plan id.
/// </summary>
public class FirmwareRolloutServiceTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:01";
    private const string PeerMac = "aa:bb:cc:dd:ee:02";

    private static PlannerDevice Ap(string mac, string name, string model = "SKU-AP1", bool upgradable = true) => new()
    {
        Mac = mac,
        Name = name,
        Model = model,
        DisplayModel = model,
        Type = DeviceType.AccessPoint,
        Upgradable = upgradable,
        FromVersion = "1.0.0",
        ToVersion = "1.1.0",
        IpAddress = "192.0.2.10",
    };

    private static FirmwareRolloutSettings Settings(Action<FirmwareRolloutSettings>? configure = null)
    {
        var settings = new FirmwareRolloutSettings
        {
            Mode = FirmwareRolloutMode.ManualOnly,
            GlobalChannel = FirmwareChannels.Release,
            IncludeUniFiNetwork = false,
            IncludeUniFiOs = false,
        };
        configure?.Invoke(settings);
        return settings;
    }

    private static RolloutHarness HarnessWithTwoAps()
    {
        var harness = new RolloutHarness();
        harness.Planning.Devices.Add(Ap(ApMac, "AP 1"));
        harness.Planning.Devices.Add(Ap(PeerMac, "AP 2"));
        return harness;
    }

    // --- Settings -------------------------------------------------------------------------------

    [Fact]
    public async Task SaveSettingsAsync_RoundTripsThroughTheStore()
    {
        using var harness = new RolloutHarness();

        await harness.Service.SaveSettingsAsync(Settings(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.GlobalChannel = FirmwareChannels.ReleaseCandidate;
            s.SpacingProfile = FirmwareSpacingProfile.Conservative;
            s.PerWaveApproval = true;
        }));

        var stored = await harness.Service.GetSettingsAsync();
        stored.Mode.Should().Be(FirmwareRolloutMode.Autopilot);
        stored.GlobalChannel.Should().Be(FirmwareChannels.ReleaseCandidate);
        stored.SpacingProfile.Should().Be(FirmwareSpacingProfile.Conservative);
        stored.PerWaveApproval.Should().BeTrue();
    }

    // --- Preview --------------------------------------------------------------------------------

    [Fact]
    public async Task BuildPreviewAsync_ChecksForUpdatesBeforePlanning()
    {
        using var harness = HarnessWithTwoAps();

        await harness.Service.BuildPreviewAsync(Settings());

        // UniFi's Check for Updates stages the builds the plan is about to command, so a preview
        // that skipped it would plan against a stale catalog.
        harness.Commands.CheckForUpdatesCalls.Should().Be(1);
        harness.Planning.ContextCalls.Should().Be(1);
    }

    [Fact]
    public async Task BuildPreviewAsync_ComposesThePlanTheWindowAndTheCounts()
    {
        using var harness = HarnessWithTwoAps();
        harness.Planning.Devices.Add(Ap("aa:bb:cc:dd:ee:03", "AP 3", upgradable: false));

        var preview = await harness.Service.BuildPreviewAsync(Settings());

        preview.Plan.Waves.Should().NotBeEmpty();
        preview.Plan.TotalEstimatedSeconds.Should().BeGreaterThan(0);
        preview.ProposedWindow.Should().BeSameAs(harness.Planning.Window);
        harness.Planning.LastEstimatedSeconds.Should().Be(preview.Plan.TotalEstimatedSeconds);
        preview.TotalDeviceCount.Should().Be(3);
        preview.UpgradableCount.Should().Be(2);
        preview.ExcludedCount.Should().Be(0);
        preview.Steps.Should().HaveCount(2);
        preview.ConsoleConnected.Should().BeTrue();
        preview.HasActivePlan.Should().BeFalse();
    }

    [Fact]
    public async Task BuildPreviewAsync_CountsExcludedDevicesSeparately()
    {
        using var harness = HarnessWithTwoAps();

        var preview = await harness.Service.BuildPreviewAsync(Settings(s =>
            s.ExclusionsJson = $"{{\"macs\":[\"{PeerMac}\"]}}"));

        preview.UpgradableCount.Should().Be(1);
        preview.ExcludedCount.Should().Be(1);
        preview.Steps.Should().Contain(s => s.State == FirmwareRolloutStepState.SkippedExcluded);
    }

    [Fact]
    public async Task BuildPreviewAsync_PersistsNothing()
    {
        using var harness = HarnessWithTwoAps();

        await harness.Service.BuildPreviewAsync(Settings(s => s.SpacingProfile = FirmwareSpacingProfile.Fast));

        (await harness.Repository.GetActivePlanAsync()).Should().BeNull();
        (await harness.Repository.GetSettingsAsync()).SpacingProfile.Should().Be(FirmwareSpacingProfile.Balanced);
    }

    [Fact]
    public async Task BuildPreviewAsync_WarnsWhenTheConsoleUpgradesDevicesItself()
    {
        using var harness = HarnessWithTwoAps();
        harness.Commands.AutoUpgradeEnabled = true;

        var preview = await harness.Service.BuildPreviewAsync(Settings());

        preview.ConsoleAutoUpgradeEnabled.Should().BeTrue();
        preview.Warnings.Should().Contain(w => w.Contains("UniFi updates devices on its own schedule"));
    }

    [Fact]
    public async Task BuildPreviewAsync_ReportsEarlyAccessAvailabilityFromTheConsole()
    {
        using var harness = HarnessWithTwoAps();

        var withoutEa = await harness.Service.BuildPreviewAsync(Settings());
        withoutEa.Channels.EarlyAccessAvailable.Should().BeFalse();

        harness.Commands.AvailableDeviceChannels = ["release", "release-candidate", "beta"];
        var withEa = await harness.Service.BuildPreviewAsync(Settings());
        withEa.Channels.EarlyAccessAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task BuildPreviewAsync_ChannelsItCannotRead_StillOffersEarlyAccess()
    {
        // An API-key connection cannot reach the device firmware setting, so the options come back
        // empty. Reading that as "no early access" hid the channel on a console whose devices run it.
        using var harness = HarnessWithTwoAps();
        harness.Commands.AvailableDeviceChannels = [];

        var preview = await harness.Service.BuildPreviewAsync(Settings(s => s.GlobalChannel = "beta"));

        preview.Channels.EarlyAccessAvailable.Should().BeTrue();
        preview.Warnings.Should().NotContain(w => w.Contains("does not offer early access"));
    }

    // --- Scheduling and starting ----------------------------------------------------------------

    [Fact]
    public async Task SchedulePlanAsync_PersistsThePlanItsStepsAndItsSettings()
    {
        using var harness = HarnessWithTwoAps();
        harness.Planning.PriorVersionUrls[ApMac] = "https://example.test/fw/ap1.bin";
        var startAt = RolloutHarness.Start.AddHours(6);

        var planId = await harness.Service.SchedulePlanAsync(
            Settings(s => s.SpacingProfile = FirmwareSpacingProfile.Conservative), startAt);

        var plan = await harness.Repository.GetPlanAsync(planId);
        plan!.Status.Should().Be(FirmwareRolloutStatus.Scheduled);
        plan.ScheduledStartAt.Should().Be(startAt);
        plan.CreatedBy.Should().Be(RolloutHarness.Actor);

        (await harness.Repository.GetStepsAsync(planId)).Should().HaveCount(2);
        (await harness.Repository.GetSettingsAsync()).SpacingProfile.Should().Be(FirmwareSpacingProfile.Conservative);

        // Prior-version images are cached while the devices are still on those versions.
        harness.Planning.PriorVersionCalls.Should().Be(1);
        var view = await harness.Service.GetActivePlanAsync();
        view!.Plan.PriorVersions.Should().Contain(p => p.Mac == ApMac && p.Url != null);
    }

    [Fact]
    public async Task SchedulePlanAsync_RefusesWhenARolloutIsAlreadyInFlight()
    {
        using var harness = HarnessWithTwoAps();
        await harness.Service.SchedulePlanAsync(Settings(), RolloutHarness.Start.AddHours(6));

        var act = () => harness.Service.SchedulePlanAsync(Settings(), RolloutHarness.Start.AddHours(12));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SchedulePlanAsync_RefusesWhenNothingHasAnUpdate()
    {
        using var harness = new RolloutHarness();
        harness.Planning.Devices.Add(Ap(ApMac, "AP 1", upgradable: false));

        var act = () => harness.Service.SchedulePlanAsync(Settings(), RolloutHarness.Start.AddHours(6));

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await harness.Repository.GetActivePlanAsync()).Should().BeNull();
    }

    [Fact]
    public async Task StartNowAsync_HandsThePlanToTheExecutor()
    {
        using var harness = HarnessWithTwoAps();

        var planId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: false);

        var plan = await harness.Repository.GetPlanAsync(planId);
        plan!.Status.Should().Be(FirmwareRolloutStatus.Running);
        plan.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StartNowAsync_DefersToTheHealthGateUnlessTheAdminOverridesIt()
    {
        using var harness = HarnessWithTwoAps();
        harness.Health.Verdict = RolloutHealthVerdict.Blocked("a critical alert is open");

        var deferredId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: false);
        var deferred = await harness.Repository.GetPlanAsync(deferredId);
        deferred!.Status.Should().Be(FirmwareRolloutStatus.Scheduled);
        deferred.StartedAt.Should().BeNull();

        await harness.Service.AbortAsync(deferredId);

        var forcedId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: true);
        (await harness.Repository.GetPlanAsync(forcedId))!.Status.Should().Be(FirmwareRolloutStatus.Running);
    }

    // --- Controls -------------------------------------------------------------------------------

    [Fact]
    public async Task PauseAndResume_MoveTheRunningPlan()
    {
        using var harness = HarnessWithTwoAps();
        var planId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: false);

        await harness.Service.PauseAsync(planId);
        (await harness.Repository.GetPlanAsync(planId))!.Status.Should().Be(FirmwareRolloutStatus.Paused);

        await harness.Service.ResumeAsync(planId);
        (await harness.Repository.GetPlanAsync(planId))!.Status.Should().Be(FirmwareRolloutStatus.Running);
    }

    [Fact]
    public async Task ResumeAsync_ReleasesTheWaveThePlanWasWaitingOnForApproval()
    {
        using var harness = new RolloutHarness();
        var document = RolloutFixtures.Document(
            RolloutFixtures.Wave(1, RolloutFixtures.PlanStep(ApMac)),
            RolloutFixtures.Wave(2, RolloutFixtures.PlanStep(PeerMac)));
        document.WaitingApprovalWave = 2;
        var plan = await harness.SeedRunningPlanAsync(document, RolloutFixtures.Step(ApMac));
        plan.Status = FirmwareRolloutStatus.Paused;
        await harness.Repository.UpdatePlanAsync(plan);

        await harness.Service.ResumeAsync(plan.Id);

        var view = await harness.Service.GetActivePlanAsync();
        view!.Status.Should().Be(FirmwareRolloutStatus.Running);
        view.Plan.ApprovedThroughWave.Should().Be(2);
        view.Plan.WaitingApprovalWave.Should().BeNull();
    }

    [Fact]
    public async Task AbortAsync_StopsThePlanAndDropsWhatHadNotStarted()
    {
        using var harness = HarnessWithTwoAps();
        var planId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: false);

        await harness.Service.AbortAsync(planId);

        (await harness.Repository.GetPlanAsync(planId))!.Status.Should().Be(FirmwareRolloutStatus.Aborted);
        (await harness.Repository.GetStepsAsync(planId))
            .Should().OnlyContain(s => s.State == FirmwareRolloutStepState.AbortedSku);
    }

    [Fact]
    public async Task PostponeAsync_PushesAWaitingPlanOutByOneWindow()
    {
        using var harness = HarnessWithTwoAps();
        var startAt = RolloutHarness.Start.AddHours(6);
        var planId = await harness.Service.SchedulePlanAsync(Settings(), startAt);

        await harness.Service.PostponeAsync(planId);

        var plan = await harness.Repository.GetPlanAsync(planId);
        plan!.ScheduledStartAt.Should().Be(startAt + FirmwareRolloutOrchestrator.HealthPostponeWindow);
        plan.Status.Should().Be(FirmwareRolloutStatus.Scheduled);
    }

    [Fact]
    public async Task PostponeAsync_RefusesOnceTheRolloutIsRunning()
    {
        using var harness = HarnessWithTwoAps();
        var planId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: false);

        var act = () => harness.Service.PostponeAsync(planId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Controls_RefuseAPlanIdThatIsNotTheOneInFlight()
    {
        using var harness = HarnessWithTwoAps();
        var planId = await harness.Service.StartNowAsync(Settings(), overrideHealthGate: false);

        var act = () => harness.Service.PauseAsync(planId + 99);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await harness.Repository.GetPlanAsync(planId))!.Status.Should().Be(FirmwareRolloutStatus.Running);
    }

    // --- Rollback and reads ---------------------------------------------------------------------

    [Fact]
    public async Task RollbackStepAsync_SendsTheDeviceBackOverSsh()
    {
        using var harness = new RolloutHarness();
        var document = RolloutFixtures.Document(RolloutFixtures.Wave(1, RolloutFixtures.PlanStep(ApMac)));
        document.PriorVersions.Add(new PlanPriorVersion
        {
            Mac = ApMac,
            Version = RolloutFixtures.FromVersion,
            Url = "https://example.test/fw/ap1.bin",
        });
        var plan = await harness.SeedRunningPlanAsync(
            document,
            RolloutFixtures.Step(ApMac, state: FirmwareRolloutStepState.LitmusPassed));
        var step = await harness.StepAsync(plan.Id, ApMac);
        harness.Observer.Set(ApMac, state: 1, firmware: RolloutFixtures.ToVersion);

        var accepted = await harness.Service.RollbackStepAsync(step.Id);

        accepted.Should().BeTrue();
        harness.Commands.SshCommands.Should().ContainSingle()
            .Which.Url.Should().Be("https://example.test/fw/ap1.bin");
    }

    [Fact]
    public async Task GetActivePlanAsync_OffersRollbackOnlyWhereAnImageWasCached()
    {
        using var harness = HarnessWithTwoAps();
        harness.Planning.PriorVersionUrls[ApMac] = "https://example.test/fw/ap1.bin";
        var planId = await harness.Service.SchedulePlanAsync(Settings(), RolloutHarness.Start.AddHours(6));

        var steps = await harness.Repository.GetStepsAsync(planId);
        foreach (var step in steps)
        {
            step.State = FirmwareRolloutStepState.LitmusPassed;
            await harness.Repository.UpdateStepAsync(step);
        }

        var view = await harness.Service.GetActivePlanAsync();
        view!.Steps.Single(s => s.Mac == ApMac).CanRollBack.Should().BeTrue();
        var peer = view.Steps.Single(s => s.Mac == PeerMac);
        peer.CanRollBack.Should().BeFalse();
        peer.RollbackUnavailableReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetPlanHistoryAsync_SummarizesPastRollouts()
    {
        using var harness = HarnessWithTwoAps();
        var planId = await harness.Service.SchedulePlanAsync(Settings(), RolloutHarness.Start.AddHours(6));

        var history = await harness.Service.GetPlanHistoryAsync();

        var row = history.Should().ContainSingle().Subject;
        row.Id.Should().Be(planId);
        row.Status.Should().Be(FirmwareRolloutStatus.Scheduled);
        row.DeviceCount.Should().Be(2);
        row.WaveCount.Should().BeGreaterThan(0);
        row.CreatedBy.Should().Be(RolloutHarness.Actor);
        row.HasReport.Should().BeFalse();
    }

    [Fact]
    public async Task GetReportAsync_SaysWhenTheRolloutIsStillSoaking()
    {
        using var harness = HarnessWithTwoAps();
        var planId = await harness.Service.SchedulePlanAsync(Settings(), RolloutHarness.Start.AddHours(6));

        var report = await harness.Service.GetReportAsync(planId);

        report!.PlanId.Should().Be(planId);
        report.IsReady.Should().BeFalse();
        report.Steps.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetReportAsync_ReturnsNullForAPlanThatDoesNotExist()
    {
        using var harness = new RolloutHarness();

        (await harness.Service.GetReportAsync(404)).Should().BeNull();
    }
}

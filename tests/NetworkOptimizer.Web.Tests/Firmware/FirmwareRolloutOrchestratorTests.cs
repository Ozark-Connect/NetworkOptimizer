using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;
using static NetworkOptimizer.Web.Tests.Firmware.RolloutFixtures;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The executor's state machine, driven the way production drives it: a scripted device timeline
/// through a real repository, so every assertion about a transition is an assertion about a
/// persisted row.
///
/// The two rules that came out of live testing are what most of this file exists to hold down.
/// An accepted command proves nothing, and neither does a reboot - only the version the console
/// reports afterwards does; and a device that never moves after being commanded gets the SSH path
/// before it gets a failure.
/// </summary>
public class FirmwareRolloutOrchestratorTests
{
    private const int Online = (int)UniFiDeviceState.Connected;
    private const int Upgrading = (int)UniFiDeviceState.Upgrading;
    private const int Offline = (int)UniFiDeviceState.Disconnected;

    /// <summary>Comfortably past the balanced profile's inter-wave gap.</summary>
    private static readonly TimeSpan PastWaveGap = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task HappyPath_RunsTheDeviceThroughToLitmusPassed()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
        harness.Commands.UpgradeCommands.Should().ContainSingle().Which.Should().Be(ApMac);

        harness.Observer.Set(ApMac, Upgrading, FromVersion, upgradeTo: ToVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Down);

        harness.Observer.Set(ApMac, Online, ToVersion);
        await harness.TickAsync(TimeSpan.FromMinutes(4));
        var backOnline = await harness.StepAsync(plan.Id, ApMac);
        backOnline.State.Should().Be(FirmwareRolloutStepState.BackOnline);
        backOnline.DowntimeSeconds.Should().BeGreaterThan(0);

        await harness.TickAsync(TimeSpan.FromSeconds(20));
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.CoolDown);

        await harness.TickAsync(FirmwareRolloutOrchestrator.CoolDown);
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.LitmusPassed);
    }

    [Fact]
    public async Task VerifiedUpgrade_FeedsTheLearnedTimingStore()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        harness.Observer.Set(ApMac, Online, ToVersion);
        await harness.TickAsync(TimeSpan.FromMinutes(4));

        var timing = await harness.Repository.GetModelTimingAsync("U6PRO");
        timing.Should().NotBeNull();
        timing!.SampleCount.Should().Be(1);
        timing.MedianDowntimeSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FullCycleOnTheWrongVersion_FailsTheStep()
    {
        // The live case this exists for: an accepted command cycled the AP and it came back on the
        // firmware it started on. rc:ok and a reboot are not evidence; the version is.
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        harness.Observer.Set(ApMac, Online, FromVersion);
        await harness.TickAsync(TimeSpan.FromMinutes(4));

        var step = await harness.StepAsync(plan.Id, ApMac);
        step.State.Should().Be(FirmwareRolloutStepState.Failed);
        step.Error.Should().Contain(FromVersion).And.Contain(ToVersion);
    }

    [Fact]
    public async Task NoTransitionInsideTheGraceWindow_RetriesOverSsh()
    {
        using var harness = new RolloutHarness();
        harness.Commands.Catalog.Add(new UniFiFirmwareCatalogEntry
        {
            BaseModel = "U6PRO",
            Version = ToVersion,
            Url = "https://fw-download.example.net/u6pro.bin",
        });
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Commands.SshCommands.Should().BeEmpty();

        // Still sitting there online well past the grace window.
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));
        harness.Commands.SshCommands.Should().ContainSingle()
            .Which.Url.Should().Be("https://fw-download.example.net/u6pro.bin");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);

        // The SSH retry took: the device cycles and comes back on target.
        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        harness.Observer.Set(ApMac, Online, ToVersion);
        await harness.TickAsync(TimeSpan.FromMinutes(4));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.BackOnline);
    }

    [Fact]
    public async Task NothingHappensAfterTheSshRetryEither_FailsTheStep()
    {
        using var harness = new RolloutHarness();
        harness.Commands.Catalog.Add(new UniFiFirmwareCatalogEntry
        {
            BaseModel = "U6PRO",
            Version = ToVersion,
            Url = "https://fw-download.example.net/u6pro.bin",
        });
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));

        var step = await harness.StepAsync(plan.Id, ApMac);
        step.State.Should().Be(FirmwareRolloutStepState.Failed);
        step.Error.Should().Contain("never started the upgrade");
    }

    [Fact]
    public async Task NoImageUrlToRetryWith_FailsRatherThanWaitingForever()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));

        var step = await harness.StepAsync(plan.Id, ApMac);
        step.State.Should().Be(FirmwareRolloutStepState.Failed);
        step.Error.Should().Contain("no image URL");
        harness.Commands.SshCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsoleCommandUnavailable_FallsThroughToTheArbitraryImageCommand()
    {
        using var harness = new RolloutHarness();
        harness.Commands.UpgradeResult = FirmwareCommandResult.NotSupported("no sample yet");
        harness.Commands.Catalog.Add(new UniFiFirmwareCatalogEntry
        {
            BaseModel = "U6PRO",
            Version = ToVersion,
            Url = "https://fw-download.example.net/u6pro.bin",
        });
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Commands.ExternalCommands.Should().ContainSingle();
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task OfflinePastItsClassBudget_PublishesCriticalAndDropsTheRestOfTheModel()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac, budgetSeconds: 900)), Wave(2, PlanStep(PeerMac))),
            Step(ApMac),
            Step(PeerMac, name: "AP 2", wave: 2));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.Set(PeerMac, Online, FromVersion, upgradeTo: ToVersion, name: "AP 2");

        await harness.TickAsync();
        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        await harness.TickAsync(TimeSpan.FromMinutes(16));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
        (await harness.StepAsync(plan.Id, PeerMac)).State.Should().Be(FirmwareRolloutStepState.AbortedSku);

        harness.Bus.Published.Should().Contain(e =>
            e.EventType == RolloutAlerts.DeviceStuckOffline && e.Severity == AlertSeverity.Critical);
        harness.Bus.Published.Should().Contain(e =>
            e.EventType == RolloutAlerts.SkuAborted && e.Severity == AlertSeverity.Warning);
        harness.Bus.Published.Where(e => e.Source == RolloutAlerts.Source)
            .Should().OnlyContain(e => e.SourceUrl == "/firmware-rollout");
    }

    [Fact]
    public async Task CloudGatewayGetsItsLongerBudget()
    {
        using var harness = new RolloutHarness();
        var gatewayPlanStep = PlanStep(GatewayMac, model: "UCGFIBER", budgetSeconds: 1800);
        gatewayPlanStep.DeviceType = "ugw";
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, gatewayPlanStep)),
            Step(GatewayMac, name: "Gateway", model: "UCGFIBER", deviceType: "ugw"));
        harness.Observer.Set(GatewayMac, Online, FromVersion, upgradeTo: ToVersion, model: "UCGFIBER", name: "Gateway");

        await harness.TickAsync();
        harness.Observer.Set(GatewayMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        // Past the 15 minute budget everything else gets, inside the cloud gateway's 30.
        await harness.TickAsync(TimeSpan.FromMinutes(16));
        (await harness.StepAsync(plan.Id, GatewayMac)).State.Should().Be(FirmwareRolloutStepState.Down);

        await harness.TickAsync(TimeSpan.FromMinutes(15));
        (await harness.StepAsync(plan.Id, GatewayMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
    }

    [Fact]
    public async Task ConsoleGoingDarkIsReadAsTheGatewayRebooting_NotAsEveryDeviceBeingDown()
    {
        using var harness = new RolloutHarness();
        var gatewayPlanStep = PlanStep(GatewayMac, model: "UCGFIBER", budgetSeconds: 1800);
        gatewayPlanStep.DeviceType = "ugw";
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, gatewayPlanStep)),
            Step(GatewayMac, name: "Gateway", model: "UCGFIBER", deviceType: "ugw"));
        harness.Observer.Set(GatewayMac, Online, FromVersion, upgradeTo: ToVersion, model: "UCGFIBER", name: "Gateway");

        await harness.TickAsync();
        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        (await harness.StepAsync(plan.Id, GatewayMac)).State.Should().Be(FirmwareRolloutStepState.Down);
    }

    [Fact]
    public async Task ConsoleGoingDark_LeavesANonGatewayStepAlone()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task HeldPeers_WaitForTheirCanaryAndThenRun()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(
                Wave(1, PlanStep(ApMac, canary: true)),
                Wave(2, PlanStep(PeerMac, held: true))),
            Step(ApMac),
            Step(PeerMac, name: "AP 2", wave: 2, state: FirmwareRolloutStepState.Held));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.Set(PeerMac, Online, FromVersion, upgradeTo: ToVersion, name: "AP 2");

        await harness.TickAsync();
        (await harness.StepAsync(plan.Id, PeerMac)).State.Should().Be(FirmwareRolloutStepState.Held);

        await RunCanaryToLitmusAsync(harness, ApMac);

        (await harness.StepAsync(plan.Id, PeerMac)).State.Should().Be(FirmwareRolloutStepState.Pending);

        await harness.TickAsync(PastWaveGap);
        (await harness.StepAsync(plan.Id, PeerMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task CanaryFailingItsLitmus_DropsTheModelAndKeepsOtherModelsRolling()
    {
        using var harness = new RolloutHarness();
        harness.Litmus.VerdictByMac[ApMac] = LitmusVerdict.Fail("CPU is pinned since the upgrade.");
        var switchPlanStep = PlanStep(SwitchMac, model: "USW24");
        switchPlanStep.DeviceType = "usw";
        var plan = await harness.SeedRunningPlanAsync(
            Document(
                Wave(1, PlanStep(ApMac, canary: true)),
                Wave(2, PlanStep(PeerMac, held: true), switchPlanStep)),
            Step(ApMac),
            Step(PeerMac, name: "AP 2", wave: 2, state: FirmwareRolloutStepState.Held),
            Step(SwitchMac, name: "Switch 1", model: "USW24", deviceType: "usw", wave: 2));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.Set(PeerMac, Online, FromVersion, upgradeTo: ToVersion, name: "AP 2");
        harness.Observer.Set(SwitchMac, Online, FromVersion, upgradeTo: ToVersion, model: "USW24", name: "Switch 1");

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
        (await harness.StepAsync(plan.Id, PeerMac)).State.Should().Be(FirmwareRolloutStepState.AbortedSku);
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.SkuAborted);

        await harness.TickAsync(PastWaveGap);
        (await harness.StepAsync(plan.Id, SwitchMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task PerWaveApproval_PausesAtTheBoundaryAndRunsOnResume()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.PerWaveApproval = true);
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        var paused = await harness.PlanAsync(plan.Id);
        paused!.Status.Should().Be(FirmwareRolloutStatus.Paused);
        JsonSerializer.Deserialize<RolloutPlanDocument>(paused.PlanJson)!.WaitingApprovalWave.Should().Be(1);
        harness.Commands.UpgradeCommands.Should().BeEmpty();
        harness.Bus.Published.Should().Contain(e =>
            e.EventType == RolloutAlerts.WaveAwaitingApproval && e.SourceUrl == "/firmware-rollout");

        await harness.Orchestrator.ResumeAsync();
        var resumed = await harness.PlanAsync(plan.Id);
        resumed!.Status.Should().Be(FirmwareRolloutStatus.Running);
        JsonSerializer.Deserialize<RolloutPlanDocument>(resumed.PlanJson)!.ApprovedThroughWave.Should().Be(1);

        await harness.TickAsync();
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task ChannelGroupNeedingAChange_CapturesTheOriginalBeforeApplyingIt()
    {
        using var harness = new RolloutHarness();
        harness.Commands.DeviceChannel = "release";
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac, channel: "release-candidate"));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        var stored = await harness.PlanAsync(plan.Id);
        var original = OriginalChannelSettings.Parse(stored!.OriginalChannelSettingsJson);
        original.Should().NotBeNull();
        original!.DeviceChannel.Should().Be("release");
        harness.Commands.ChannelWrites.Should().ContainSingle().Which.Should().Be("release-candidate");
        // Setting a channel is only half of it: the catalog has to be re-read for that channel's builds.
        harness.Commands.CheckForUpdatesCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FinishingARollout_PutsTheChannelBackAndAnnouncesCompletion()
    {
        using var harness = new RolloutHarness();
        harness.Commands.DeviceChannel = "release";
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac, channel: "release-candidate"));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        var stored = await harness.PlanAsync(plan.Id);
        stored!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
        stored.OriginalChannelSettingsJson.Should().BeNull();
        harness.Commands.DeviceChannel.Should().Be("release");
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.Completed);
    }

    [Fact]
    public async Task Abort_PutsTheChannelBackAndDropsWhatHadNotStarted()
    {
        using var harness = new RolloutHarness();
        harness.Commands.DeviceChannel = "release";
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac)), Wave(2, PlanStep(PeerMac))),
            Step(ApMac, channel: "release-candidate"),
            Step(PeerMac, name: "AP 2", wave: 2, channel: "release-candidate"));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.Set(PeerMac, Online, FromVersion, upgradeTo: ToVersion, name: "AP 2");

        await harness.TickAsync();
        await harness.Orchestrator.AbortAsync("the operator stopped it");

        var stored = await harness.PlanAsync(plan.Id);
        stored!.Status.Should().Be(FirmwareRolloutStatus.Aborted);
        stored.OriginalChannelSettingsJson.Should().BeNull();
        harness.Commands.DeviceChannel.Should().Be("release");
        (await harness.StepAsync(plan.Id, PeerMac)).State.Should().Be(FirmwareRolloutStepState.AbortedSku);
    }

    [Fact]
    public async Task ARolloutThatDiedWithTheChannelChanged_IsPutBackOnTheNextPass()
    {
        using var harness = new RolloutHarness();
        harness.Commands.DeviceChannel = "beta";
        await harness.Repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.Failed,
            PlanJson = "{}",
            CreatedBy = "TestUser",
            OriginalChannelSettingsJson = JsonSerializer.Serialize(
                new OriginalChannelSettings { DeviceChannel = "release" }),
        });

        await harness.TickAsync();

        harness.Commands.DeviceChannel.Should().Be("release");
        var history = await harness.Repository.GetPlanHistoryAsync();
        history.Single().OriginalChannelSettingsJson.Should().BeNull();
    }

    [Fact]
    public async Task ResumingAPlanMidCycle_PicksTheStepUpFromItsPersistedState()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac, state: FirmwareRolloutStepState.Down));

        // The row says Down and the device is back on the target: the pass must finish the step
        // rather than re-command it.
        var down = await harness.StepAsync(plan.Id, ApMac);
        down.CommandedAt = RolloutHarness.Start;
        down.WentDownAt = RolloutHarness.Start;
        await harness.Repository.UpdateStepAsync(down);
        harness.Observer.Set(ApMac, Online, ToVersion);

        await harness.TickAsync(TimeSpan.FromMinutes(4));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.BackOnline);
        harness.Commands.UpgradeCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduledPlan_StartsWhenItsTimeComes()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            RolloutHarness.Start.AddHours(1),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Scheduled);

        await harness.TickAsync(TimeSpan.FromHours(1));
        var started = await harness.PlanAsync(plan.Id);
        started!.Status.Should().Be(FirmwareRolloutStatus.Running);
        started.StartedAt.Should().NotBeNull();
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.Started);
    }

    [Fact]
    public async Task ScheduledStartOnAnUnhealthySite_PostponesOneWindowAndSaysWhy()
    {
        using var harness = new RolloutHarness();
        harness.Health.Verdict = RolloutHealthVerdict.Blocked("a critical alert is open (WAN Outage)");
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            RolloutHarness.Start,
            Step(ApMac));

        await harness.TickAsync();

        var stored = await harness.PlanAsync(plan.Id);
        stored!.Status.Should().Be(FirmwareRolloutStatus.Scheduled);
        stored.ScheduledStartAt.Should().Be(RolloutHarness.Start + FirmwareRolloutOrchestrator.HealthPostponeWindow);
        harness.Bus.Published.Should().ContainSingle(e => e.EventType == RolloutAlerts.PostponedHealth)
            .Which.Message.Should().Contain("WAN Outage");
    }

    [Fact]
    public async Task ManualStart_OverridesTheHealthGate()
    {
        using var harness = new RolloutHarness();
        harness.Health.Verdict = RolloutHealthVerdict.Blocked("a critical alert is open (WAN Outage)");
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            RolloutHarness.Start.AddDays(1),
            Step(ApMac));

        var started = await harness.Orchestrator.StartNowAsync(plan.Id, overrideHealthGate: true);

        started.Should().BeTrue();
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);
    }

    [Fact]
    public async Task PreFlightBackupWithNoSampleYet_NotesItAndCarriesOn()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            RolloutHarness.Start,
            Step(ApMac));

        await harness.TickAsync();

        harness.Commands.BackupCalls.Should().Be(1);
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);
        harness.Bus.Published.Should().Contain(e => e.Message.Contains("could not take a console backup"));
    }

    [Fact]
    public async Task WaivedBackup_IsNotEvenAttempted()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.WaiveBackup = true);
        await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            RolloutHarness.Start,
            Step(ApMac));

        await harness.TickAsync();

        harness.Commands.BackupCalls.Should().Be(0);
    }

    [Fact]
    public async Task StandaloneConsole_IsNeverGivenAUniFiOsUpdate()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Name = "Console",
            Firmware = new UniFiConsoleFirmware
            {
                ReleaseChannel = "release",
                Latest = new UniFiConsoleFirmwareRelease { Product = UniFiConsoleSystemInfo.StandaloneConsoleProduct },
            },
        };
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiOsUpdate = true;
        var plan = await harness.SeedScheduledPlanAsync(document, RolloutHarness.Start, Step(ApMac));

        await harness.TickAsync();

        var stored = await harness.PlanAsync(plan.Id);
        var persisted = JsonSerializer.Deserialize<RolloutPlanDocument>(stored!.PlanJson)!;
        persisted.IncludesUniFiOsUpdate.Should().BeFalse();
        persisted.Notes.Should().Contain(n => n.Contains("UniFi OS is not updated on a self-hosted console"));
    }

    [Fact]
    public async Task MeshRepair_IsQueuedOnceBothHalvesArePast()
    {
        using var harness = new RolloutHarness();
        var document = Document(Wave(1, PlanStep(ApMac)), Wave(2, PlanStep(PeerMac)));
        document.MeshRepairs.Add(new PlanMeshRepair
        {
            ChildMac = ApMac,
            ChildName = "AP 1",
            ChildIp = "192.0.2.10",
            ParentMac = PeerMac,
            Iface = "vwiresta0",
            AfterWave = 2,
        });
        var plan = await harness.SeedRunningPlanAsync(
            document,
            Step(ApMac),
            Step(PeerMac, name: "AP 2", wave: 2));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.Set(PeerMac, Online, FromVersion, upgradeTo: ToVersion, name: "AP 2");

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);

        // The child is through but the parent has not even started.
        harness.Mesh.Enqueued.Should().BeEmpty();

        await harness.TickAsync(PastWaveGap);
        await RunCanaryToLitmusAsync(harness, PeerMac);

        harness.Mesh.Enqueued.Should().ContainSingle()
            .Which.Should().Be(("192.0.2.10", "vwiresta0", "AP 1"));
    }

    [Fact]
    public async Task MeshRepair_IsNotQueuedForAPairThatFailed()
    {
        using var harness = new RolloutHarness();
        harness.Litmus.Verdict = LitmusVerdict.Fail("no health response at all");
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.MeshRepairs.Add(new PlanMeshRepair
        {
            ChildMac = ApMac,
            ChildName = "AP 1",
            ChildIp = "192.0.2.10",
            Iface = "vwiresta0",
            AfterWave = 1,
        });
        await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Mesh.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task ResourceUseJumpingAfterTheUpgrade_FlagsARegressionAndWarns()
    {
        using var harness = new RolloutHarness();
        harness.Litmus.Stats = new RolloutResourceStats { CpuPercent = 12, MemoryUsedPercent = 40, SampleCount = 20 };
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.LitmusPassed);

        // The post window reads much heavier than the pre window did.
        harness.Litmus.Stats = new RolloutResourceStats { CpuPercent = 40, MemoryUsedPercent = 42, SampleCount = 20 };
        await harness.TickAsync(FirmwareRolloutOrchestrator.ResourceWindow + TimeSpan.FromMinutes(1));

        var step = await harness.StepAsync(plan.Id, ApMac);
        step.State.Should().Be(FirmwareRolloutStepState.RegressionFlagged);
        step.PostStatsJson.Should().NotBeNull();
        var alert = harness.Bus.Published.Single(e => e.EventType == RolloutAlerts.ResourceRegression);
        alert.Severity.Should().Be(AlertSeverity.Warning);
        alert.Message.Should().Contain("U6PRO").And.Contain(FromVersion).And.Contain(ToVersion);
    }

    [Fact]
    public async Task ResourceUseDroppingAfterTheUpgrade_IsAQuietNote()
    {
        using var harness = new RolloutHarness();
        harness.Litmus.Stats = new RolloutResourceStats { CpuPercent = 60, MemoryUsedPercent = 40, SampleCount = 20 };
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);

        harness.Litmus.Stats = new RolloutResourceStats { CpuPercent = 20, MemoryUsedPercent = 40, SampleCount = 20 };
        await harness.TickAsync(FirmwareRolloutOrchestrator.ResourceWindow + TimeSpan.FromMinutes(1));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.LitmusPassed);
        harness.Bus.Published.Single(e => e.EventType == RolloutAlerts.ResourceImprovement)
            .Severity.Should().Be(AlertSeverity.Info);
    }

    [Fact]
    public async Task Rollback_GoesOverSshFirstAndPutsTheStepBackThroughTheMachine()
    {
        using var harness = new RolloutHarness();
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.PriorVersions.Add(new PlanPriorVersion
        {
            Mac = ApMac,
            Version = FromVersion,
            Url = "https://fw-download.example.net/u6pro-old.bin",
        });
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);

        var passed = await harness.StepAsync(plan.Id, ApMac);
        var rolledBack = await harness.Orchestrator.RollbackStepAsync(passed.Id);

        rolledBack.Should().BeTrue();
        harness.Commands.SshCommands.Should().ContainSingle()
            .Which.Url.Should().Be("https://fw-download.example.net/u6pro-old.bin");
        harness.Commands.ExternalCommands.Should().BeEmpty();

        var step = await harness.StepAsync(plan.Id, ApMac);
        step.State.Should().Be(FirmwareRolloutStepState.Commanded);
        step.ToVersion.Should().Be(FromVersion);
        step.FromVersion.Should().Be(ToVersion);
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.RollbackExecuted);
    }

    [Fact]
    public async Task Rollback_RefusesWhenNoPriorImageWasCached()
    {
        using var harness = new RolloutHarness();
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.PriorVersions.Add(new PlanPriorVersion
        {
            Mac = ApMac,
            Version = FromVersion,
            UnavailableReason = "the public release feed carries no such build (it serves GA only)",
        });
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunCanaryToLitmusAsync(harness, ApMac);
        var passed = await harness.StepAsync(plan.Id, ApMac);

        (await harness.Orchestrator.RollbackStepAsync(passed.Id)).Should().BeFalse();
        harness.Commands.SshCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task SuppressionWindowIsRefreshedWhileADeviceIsMidCycleAndClearedWhenItSettles()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Suppression.IsInRolloutWindow(SiteManagementService.DefaultSiteSlug, ApMac, harness.Time.GetUtcNow().UtcDateTime)
            .Should().BeTrue();

        await RunCanaryToLitmusAsync(harness, ApMac);

        harness.Suppression.IsInRolloutWindow(SiteManagementService.DefaultSiteSlug, ApMac, harness.Time.GetUtcNow().UtcDateTime)
            .Should().BeFalse();
    }

    [Fact]
    public async Task SuppressionIsNotArmedWhenTheSiteTurnedItOff()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SuppressStandardAlerts = false);
        await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Suppression.IsInRolloutWindow(SiteManagementService.DefaultSiteSlug, ApMac, harness.Time.GetUtcNow().UtcDateTime)
            .Should().BeFalse();
    }

    [Fact]
    public async Task EveryTransitionIsPersistedAsItHappens()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        var seen = new List<FirmwareRolloutStepState>();
        async Task RecordAsync()
        {
            using var verify = harness.NewContext();
            var row = verify.FirmwareRolloutSteps.Single(s => s.PlanId == plan.Id);
            if (seen.Count == 0 || seen[^1] != row.State) seen.Add(row.State);
        }

        await harness.TickAsync();
        await RecordAsync();

        harness.Observer.Set(ApMac, Upgrading, FromVersion, upgradeTo: ToVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        await RecordAsync();

        harness.Observer.Set(ApMac, Online, ToVersion);
        await harness.TickAsync(TimeSpan.FromMinutes(4));
        await RecordAsync();

        await harness.TickAsync(TimeSpan.FromSeconds(20));
        await RecordAsync();

        await harness.TickAsync(FirmwareRolloutOrchestrator.CoolDown);
        await RecordAsync();

        seen.Should().Equal(
            FirmwareRolloutStepState.Commanded,
            FirmwareRolloutStepState.Down,
            FirmwareRolloutStepState.BackOnline,
            FirmwareRolloutStepState.CoolDown,
            FirmwareRolloutStepState.LitmusPassed);
    }

    /// <summary>Walks a commanded device through its whole cycle to whatever the litmus says.</summary>
    private static async Task RunCanaryToLitmusAsync(RolloutHarness harness, string mac)
    {
        var existing = harness.Observer.Devices[mac];
        harness.Observer.Devices[mac] = existing with { State = Offline };
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Observer.Devices[mac] = existing with { State = Online, Firmware = ToVersion, UpgradeToFirmware = null };
        await harness.TickAsync(TimeSpan.FromMinutes(4));
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        await harness.TickAsync(FirmwareRolloutOrchestrator.CoolDown);
    }
}

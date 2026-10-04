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
    public async Task BuildPreviewAsync_DropsADeviceTheConsoleOffersAnOlderBuild()
    {
        // A console on a less aggressive channel than a device presents its older build as an
        // update. That is a downgrade, and it never earns a step.
        using var harness = HarnessWithTwoAps();
        harness.Planning.Devices.Add(OlderOffer("aa:bb:cc:dd:ee:03"));

        var preview = await harness.Service.BuildPreviewAsync(Settings());

        LiveMacs(preview).Should().BeEquivalentTo(new[] { ApMac, PeerMac });
    }

    [Fact]
    public async Task BuildPreviewAsync_ReadOnly_DropsADeviceTheConsoleOffersAnOlderBuild()
    {
        // The drift check previews read-only, without channel staging. That path used to keep the
        // older offer, count the device as new firmware, and open Re-plan on a downgrade.
        using var harness = HarnessWithTwoAps();
        harness.Planning.Devices.Add(OlderOffer("aa:bb:cc:dd:ee:03"));

        var preview = await harness.Service.BuildPreviewAsync(Settings(), readOnly: true);

        LiveMacs(preview).Should().BeEquivalentTo(new[] { ApMac, PeerMac });
    }

    private static PlannerDevice OlderOffer(string mac) => new()
    {
        Mac = mac,
        Name = "Bridge",
        Model = "SKU-BRIDGE",
        DisplayModel = "SKU-BRIDGE",
        Type = DeviceType.AccessPoint,
        Upgradable = true,
        FromVersion = "6.5.89",
        ToVersion = "6.5.87",
        IpAddress = "192.0.2.11",
    };

    private static IEnumerable<string> LiveMacs(RolloutPreviewView preview) =>
        preview.Steps.Where(s => s.State != FirmwareRolloutStepState.SkippedExcluded).Select(s => s.Mac);

    [Fact]
    public async Task BuildPreviewAsync_ShowsANetworkAppUpdateAdoptedFromTheSharedCatalog()
    {
        using var harness = HarnessWithTwoAps();
        // Each /api/system read is a fresh snapshot, as with the real client: the adoption lives
        // only on the plan's own console object, so the preview must read that one, not re-fetch.
        harness.Commands.SnapshotConsoleInfoPerRead = true;
        // The console has not noticed the update itself; the shared catalog is offering it.
        harness.Commands.ConsoleInfo!.NetworkApplication!.UpdateAvailable = null;
        await harness.SharedCatalog.UpsertNetworkAppBuildAsync("release", "9.1.0", null);

        var preview = await harness.Service.BuildPreviewAsync(Settings(s => s.IncludeUniFiNetwork = true));

        preview.Plan.IncludesUniFiNetworkUpdate.Should().BeTrue();
        preview.NetworkApplication.Should().NotBeNull();
        preview.NetworkApplication!.TargetVersion.Should().Be("9.1.0");
        preview.NetworkApplication.UpdateAvailable.Should().BeTrue();
    }

    // --- Shared device builds -------------------------------------------------------------------

    private static Task SeedSharedDeviceBuildAsync(RolloutHarness harness, string version) =>
        harness.SharedCatalog.UpsertDeviceBuildsAsync(
        [
            new SharedFirmwareBuild
            {
                Model = "SKU-AP1", Channel = FirmwareChannels.Release, Version = version,
                Url = $"https://example.test/unifi-firmware/SKU-AP1-{version}.bin",
            },
        ]);

    [Fact]
    public async Task BuildPreviewAsync_ANewerSharedDeviceBuildReplacesTheConsolesOlderOffer()
    {
        using var harness = HarnessWithTwoAps();
        await SeedSharedDeviceBuildAsync(harness, "1.2.0");

        var preview = await harness.Service.BuildPreviewAsync(Settings());

        preview.Plan.Waves.SelectMany(w => w.Steps).Where(s => s.Mac == ApMac)
            .Should().ContainSingle().Which.ToVersion.Should().Be("1.2.0");
        preview.Plan.TargetImages.Should().ContainSingle(i => i.Mac == ApMac)
            .Which.Url.Should().Be("https://example.test/unifi-firmware/SKU-AP1-1.2.0.bin",
                "the image must name the new target, or the step installs the console's own build");
    }

    [Fact]
    public async Task BuildPreviewAsync_AnOlderSharedDeviceBuildLeavesTheConsolesOffer()
    {
        using var harness = HarnessWithTwoAps();
        await SeedSharedDeviceBuildAsync(harness, "1.0.5");

        var preview = await harness.Service.BuildPreviewAsync(Settings());

        preview.Plan.Waves.SelectMany(w => w.Steps).Where(s => s.Mac == ApMac)
            .Should().ContainSingle().Which.ToVersion.Should().Be("1.1.0");
    }

    // --- Shared UniFi OS builds -----------------------------------------------------------------

    private const string Platform = "UCGF";
    private const string SharedUrl = "https://example.test/unifi-dream/UCGF-6.0.11.bin";

    /// <summary>
    /// A UCG-Fiber site on Early Access whose console offers 6.0.9 while running 6.0.7. Each
    /// /api/system read is a fresh snapshot, so an adoption only lives on the plan's own object.
    /// </summary>
    private static RolloutHarness CloudGatewayHarness(string platform = Platform)
    {
        var harness = HarnessWithTwoAps();
        harness.Planning.Devices.Add(new PlannerDevice
        {
            Mac = "aa:bb:cc:dd:ee:04",
            Name = "Gateway",
            Model = "UDMA6A8",
            DisplayModel = "UDMA6A8",
            Type = DeviceType.Gateway,
            Upgradable = false,
            FromVersion = "6.0.7",
            IpAddress = "192.0.2.1",
        });
        harness.Commands.SnapshotConsoleInfoPerRead = true;
        harness.Commands.ConsoleInfo = RolloutFixtures.Console(osChannel: "beta", installedOs: "6.0.7");
        harness.Commands.ConsoleInfo.Hardware!.Shortname = platform;
        harness.Commands.ConsoleInfo.Firmware!.LatestByChannel["beta"] = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease
        {
            Channel = "beta",
            Version = "v6.0.9+bbb2222",
            Created = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            Links = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLinks
            {
                Data = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLink { Href = "https://example.test/unifi-dream/UCGF-6.0.9.bin" },
            },
        };
        return harness;
    }

    private static FirmwareRolloutSettings EarlyAccessOs() => Settings(s =>
    {
        s.GlobalChannel = FirmwareChannels.Beta;
        s.IncludeUniFiOs = true;
    });

    private static Task SeedSharedOsBuildAsync(RolloutHarness harness, string platform = Platform, string channel = "beta") =>
        harness.SharedCatalog.UpsertUniFiOsBuildsAsync(
        [
            new SharedUniFiOsBuild
            {
                Platform = platform,
                Channel = channel,
                Version = "v6.0.11+ccc3333",
                Url = SharedUrl,
                PublishedUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            },
        ]);

    [Fact]
    public async Task BuildPreviewAsync_RecordsEveryChannelsUniFiOsBuildUnderThePlatform()
    {
        using var harness = CloudGatewayHarness();

        await harness.Service.BuildPreviewAsync(EarlyAccessOs());

        // Release may also be recorded: the public feed patches a stale GA entry before this runs.
        using var db = harness.NewContext();
        var row = db.SharedUniFiOsBuilds.Should().ContainSingle(b => b.Channel == "beta").Subject;
        row.Platform.Should().Be(Platform);
        row.Version.Should().Be("v6.0.9+bbb2222");
        row.Url.Should().Be("https://example.test/unifi-dream/UCGF-6.0.9.bin");
        row.PublishedUtc.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task BuildPreviewAsync_AdoptsANewerUniFiOsBuildAnotherConsoleWasOffered()
    {
        using var harness = CloudGatewayHarness();
        await SeedSharedOsBuildAsync(harness);

        var preview = await harness.Service.BuildPreviewAsync(EarlyAccessOs());

        preview.UniFiOs!.TargetVersion.Should().Be("v6.0.11+ccc3333");
        preview.UniFiOs.UpdateAvailable.Should().BeTrue();
        preview.Plan.IncludesUniFiOsUpdate.Should().BeTrue();
        preview.Plan.UniFiOsUpdate.TargetVersion.Should().Be("v6.0.11+ccc3333");
        preview.Plan.UniFiOsUpdate.Url.Should().Be(SharedUrl);
        preview.Plan.UniFiOsUpdate.PublishedAt.Should().Be(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task BuildPreviewAsync_AdoptsASharedBuildFromALessAggressiveChannel()
    {
        // Early Access sees every channel at or below it, so a newer RC from elsewhere qualifies.
        using var harness = CloudGatewayHarness();
        await SeedSharedOsBuildAsync(harness, channel: "release-candidate");

        var preview = await harness.Service.BuildPreviewAsync(EarlyAccessOs());

        preview.Plan.UniFiOsUpdate.TargetVersion.Should().Be("v6.0.11+ccc3333");
        preview.UniFiOs!.TargetVersion.Should().Be("v6.0.11+ccc3333", "the preview names the build the plan installs");
    }

    [Fact]
    public async Task BuildPreviewAsync_KeepsTheConsolesOwnBuildWithoutGatewaySsh()
    {
        // The console has not staged the shared build, so SSH by URL is the only way to install it.
        using var harness = CloudGatewayHarness();
        harness.Commands.GatewaySshConfigured = false;
        await SeedSharedOsBuildAsync(harness);

        var preview = await harness.Service.BuildPreviewAsync(EarlyAccessOs());

        preview.Plan.UniFiOsUpdate.TargetVersion.Should().Be("v6.0.9+bbb2222");
    }

    [Fact]
    public async Task BuildPreviewAsync_IgnoresASharedBuildForAnotherPlatform()
    {
        using var harness = CloudGatewayHarness();
        await SeedSharedOsBuildAsync(harness, platform: "UDMPRO");

        var preview = await harness.Service.BuildPreviewAsync(EarlyAccessOs());

        preview.Plan.UniFiOsUpdate.TargetVersion.Should().Be("v6.0.9+bbb2222");
    }

    [Fact]
    public async Task BuildPreviewAsync_IgnoresASharedBuildFromAMoreAggressiveChannel()
    {
        // A site on Official never takes an Early Access build, wherever it was seen.
        using var harness = CloudGatewayHarness();
        await SeedSharedOsBuildAsync(harness);

        var preview = await harness.Service.BuildPreviewAsync(Settings(s =>
        {
            s.GlobalChannel = FirmwareChannels.Release;
            s.IncludeUniFiOs = true;
        }));

        preview.Plan.UniFiOsUpdate.TargetVersion.Should().NotBe("v6.0.11+ccc3333");
    }

    [Fact]
    public async Task BuildPreviewAsync_ASelfHostedConsoleNeitherRecordsNorAdoptsUniFiOsBuilds()
    {
        using var harness = CloudGatewayHarness();
        harness.Commands.ConsoleInfo!.Firmware!.Latest = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease
        {
            Product = NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo.StandaloneConsoleProduct,
        };
        await SeedSharedOsBuildAsync(harness, platform: "OTHER");

        var preview = await harness.Service.BuildPreviewAsync(EarlyAccessOs());

        using var db = harness.NewContext();
        db.SharedUniFiOsBuilds.Should().ContainSingle(b => b.Platform == "OTHER", "only the seeded row exists");
        preview.UniFiOs.Should().BeNull("a self-hosted console has no UniFi OS step");
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

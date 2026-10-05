using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;
using static NetworkOptimizer.Web.Tests.Firmware.RolloutFixtures;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The two console-level channels a rollout puts in force: the UniFi Network application's, ahead
/// of the wave-0 application update, and UniFi OS's, ahead of the console's own update.
///
/// One release channel drives everything, so an unset per-surface channel follows the global one;
/// the per-surface settings are overrides. Both channels are readable from /api/system, so both are
/// captured before they are written and put back at the end - and a surface this rollout does not
/// update is a surface it does not re-channel.
/// </summary>
public class RolloutConsoleChannelTests
{
    private const int Online = (int)UniFiDeviceState.Connected;

    private static RolloutPlanDocument NetworkAppPlan()
    {
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiNetworkUpdate = true;
        return document;
    }

    private static RolloutPlanDocument UniFiOsPlan()
    {
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiOsUpdate = true;
        return document;
    }

    private static RolloutPlanDocument BothConsoleUpdatesPlan()
    {
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiNetworkUpdate = true;
        document.IncludesUniFiOsUpdate = true;
        return document;
    }

    private static RolloutPlanDocument Stored(FirmwareRolloutPlan plan) =>
        JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson)!;

    // --- The UniFi Network application ----------------------------------------------------------

    [Fact]
    public async Task NetworkAppChannel_FollowsTheGlobalChannel_AndIsSetBeforeTheUpdate()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "release", appUpdateAvailable: "10.7.10");
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.ReleaseCandidate);
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        var write = harness.Commands.ConsoleChannelWrites.Should().ContainSingle().Subject;
        write.NetworkApp.Should().Be(FirmwareChannels.ReleaseCandidate);
        write.UniFiOs.Should().BeNull("the UniFi OS channel is not part of this rollout");
        harness.Commands.Calls.IndexOf("console-channels")
            .Should().BeLessThan(harness.Commands.Calls.IndexOf("network-app-update"));

        var stored = await harness.PlanAsync(plan.Id);
        Stored(stored!).ConsoleChannels.NetworkAppChannel.Should().Be(FirmwareChannels.ReleaseCandidate);
        OriginalChannelSettings.Parse(stored!.OriginalChannelSettingsJson)!
            .NetworkAppChannel.Should().Be("release", "the original has to be captured before it is written");
    }

    [Fact]
    public async Task AnExplicitNetworkAppOverride_WinsOverTheGlobalChannel()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "release", appUpdateAvailable: "10.7.10");
        await harness.WithSettingsAsync(s =>
        {
            s.GlobalChannel = FirmwareChannels.Release;
            s.NetworkAppChannel = FirmwareChannels.Beta;
        });
        await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Commands.ConsoleChannelWrites.Should().ContainSingle().Subject
            .NetworkApp.Should().Be(FirmwareChannels.Beta);
    }

    [Fact]
    public async Task AnApplicationAlreadyOnTheChannel_IsNotRewritten()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "release", appUpdateAvailable: "10.7.10");
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Release);
        await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Commands.ConsoleChannelWrites.Should().BeEmpty();
        harness.Commands.NetworkAppUpdateCalls.Should().Be(1);
    }

    [Fact]
    public async Task AnApplicationLeftOutOfTheRollout_KeepsItsChannel()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "release", appUpdateAvailable: "10.7.10");
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Commands.ConsoleChannelWrites.Should().BeEmpty();
        (await harness.PlanAsync(plan.Id))!.OriginalChannelSettingsJson.Should().BeNull();
    }

    [Fact]
    public async Task NothingOnOffer_SettlesWaveZeroWithoutInstallingAnything()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "release", appUpdateAvailable: null);
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Release);
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Commands.ApplicationUpdateChecks.Should().BeGreaterThan(0);
        harness.Commands.NetworkAppUpdateCalls.Should().Be(0);
        Stored((await harness.PlanAsync(plan.Id))!).NetworkAppUpdate.Outcome.Should().Be("nothing-to-update");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    // --- UniFi OS -------------------------------------------------------------------------------

    [Fact]
    public async Task UniFiOsChannel_IsSetBeforeTheConsoleUpdateIsCommanded()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "release");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        var write = harness.Commands.ConsoleChannelWrites.Should().ContainSingle().Subject;
        write.UniFiOs.Should().Be(FirmwareChannels.Beta);
        write.NetworkApp.Should().BeNull("the application is not part of this rollout");
        harness.Commands.Calls.IndexOf("console-channels")
            .Should().BeLessThan(harness.Commands.Calls.IndexOf("unifi-os-update"));

        var stored = await harness.PlanAsync(plan.Id);
        Stored(stored!).ConsoleChannels.UniFiOsChannel.Should().Be(FirmwareChannels.Beta);
        OriginalChannelSettings.Parse(stored!.OriginalChannelSettingsJson)!.UniFiOsChannel.Should().Be("release");
    }

    /// <summary>
    /// A console on beta whose plan wants release-candidate. Whether the offer is taken must
    /// depend on the switch going through, not on what happens to be on offer afterwards.
    /// </summary>
    private static async Task<FirmwareRolloutPlan> SeedRefusedChannelPlanAsync(
        RolloutHarness harness, string plannedOs, string offeredOs, string? plannedUrl = null)
    {
        harness.Commands.ConsoleInfo = Console(osChannel: "beta");
        harness.Commands.ConsoleChannelsAccepted = false;
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = offeredOs };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.ReleaseCandidate);
        var document = UniFiOsPlan();
        document.UniFiOsUpdate.TargetVersion = plannedOs;
        document.UniFiOsUpdate.Url = plannedUrl;
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        return plan;
    }

    [Fact]
    public async Task ARefusedChannelSwitch_DoesNotInstallAnotherChannelsBuild()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedRefusedChannelPlanAsync(harness, plannedOs: "v4.3.6+abc1234", offeredOs: "v5.0.0+def5678");

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        // The first write is the switch; a later one is the completed plan putting beta back.
        harness.Commands.ConsoleChannelWrites.First().UniFiOs.Should().Be(FirmwareChannels.ReleaseCandidate);
        harness.Commands.UniFiOsUpdateCalls.Should().Be(0, "the offer is the beta build, not the planned one");
        harness.Commands.Calls.Should().NotContain("ssh-unifi-os-update", "the plan captured no image to fall back to");
        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.UniFiOsUpdate.Outcome.Should().Be("refused");
        stored.ConsoleChannels.UniFiOsChannel.Should().BeNull("a refused switch is not one this rollout put in force");
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.UniFiOsUpdateRefused);
    }

    [Fact]
    public async Task ARefusedChannelSwitch_FallsBackToThePlannedImageOverSsh()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedRefusedChannelPlanAsync(
            harness, plannedOs: "v4.3.6+abc1234", offeredOs: "v5.0.0+def5678", plannedUrl: "https://example.test/os-4.3.6.bin");

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        harness.Commands.Calls.Should().Contain("ssh-unifi-os-update");
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Triggered.Should().BeTrue();
    }

    [Fact]
    public async Task ARefusedChannelSwitch_StillInstallsWhenTheOfferIsThePlannedBuild()
    {
        using var harness = new RolloutHarness();
        // Catalog shapes on both sides: the same build carries the same hash, and a comparison
        // that trips on the hash would refuse the very build the plan chose.
        var plan = await SeedRefusedChannelPlanAsync(harness, plannedOs: "v4.3.6+abc1234", offeredOs: "v4.3.6+abc1234");

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(1, "the console is offering exactly the build the plan captured");
        harness.Bus.Published.Should().NotContain(e => e.EventType == RolloutAlerts.UniFiOsUpdateRefused);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Triggered.Should().BeTrue();
    }

    [Fact]
    public async Task AStandaloneConsole_KeepsItsUniFiOsChannel()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "release", standalone: true);
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.ConsoleChannelWrites.Should().BeEmpty();
        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("refused");
    }

    // --- Putting them back ----------------------------------------------------------------------

    [Fact]
    public async Task FinishingARollout_PutsBothConsoleChannelsBack()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "release", appChannel: "release", appUpdateAvailable: "10.7.10");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var plan = await harness.SeedScheduledPlanAsync(BothConsoleUpdatesPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        // The app restart lands its new build; a console still answering on the old version
        // would hold wave 1 (and the whole rollout) open.
        harness.Commands.ConsoleInfo!.NetworkApplication!.Version = "10.7.10";
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        await RunDeviceToLitmusAsync(harness, ApMac);
        await harness.TickAsync(TimeSpan.FromMinutes(5));

        var stored = await harness.PlanAsync(plan.Id);
        stored!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
        stored.OriginalChannelSettingsJson.Should().BeNull();

        var restore = harness.Commands.ConsoleChannelWrites.Last();
        restore.NetworkApp.Should().Be("release");
        restore.UniFiOs.Should().Be("release");
        harness.Commands.ConsoleInfo!.NetworkApplication!.ReleaseChannel.Should().Be("release");
        harness.Commands.ConsoleInfo.Firmware!.ReleaseChannel.Should().Be("release");
    }

    [Fact]
    public async Task Abort_PutsBackWhateverHadBeenChanged()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "release", appUpdateAvailable: "10.7.10");
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.Orchestrator.AbortAsync("the operator stopped it");

        var restore = harness.Commands.ConsoleChannelWrites.Last();
        restore.NetworkApp.Should().Be("release");
        restore.UniFiOs.Should().BeNull("the UniFi OS channel was never changed, so it is not put back");
        (await harness.PlanAsync(plan.Id))!.OriginalChannelSettingsJson.Should().BeNull();
    }

    [Fact]
    public async Task ARolloutThatDiedWithTheConsoleChannelsChanged_IsPutBackOnTheNextPass()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "beta", appChannel: "beta");
        await harness.Repository.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.Failed,
            PlanJson = "{}",
            CreatedBy = "TestUser",
            OriginalChannelSettingsJson = JsonSerializer.Serialize(
                new OriginalChannelSettings { NetworkAppChannel = "release", UniFiOsChannel = "release" }),
        });

        await harness.TickAsync();

        var restore = harness.Commands.ConsoleChannelWrites.Should().ContainSingle().Subject;
        restore.NetworkApp.Should().Be("release");
        restore.UniFiOs.Should().Be("release");
        (await harness.Repository.GetPlanHistoryAsync()).Single().OriginalChannelSettingsJson.Should().BeNull();
    }

    [Fact]
    public async Task AResumedRollout_DoesNotSetAChannelItHasAlreadySet()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "release");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);

        var document = UniFiOsPlan();
        document.ConsoleChannels.UniFiOsChannel = FirmwareChannels.Beta;
        await harness.SeedRunningPlanAsync(document, Step(ApMac, state: FirmwareRolloutStepState.LitmusPassed));

        await harness.TickAsync();

        harness.Commands.ConsoleChannelWrites.Should().BeEmpty();
        harness.Commands.UniFiOsUpdateCalls.Should().Be(1);
    }

    /// <summary>
    /// A plan whose UniFi OS target came from another site's console: newer than the build this
    /// console offers itself, with the image URL captured at plan time.
    /// </summary>
    private static async Task<FirmwareRolloutPlan> SeedSharedBuildPlanAsync(RolloutHarness harness)
    {
        harness.Commands.ConsoleInfo = Console(osChannel: "beta", installedOs: "6.0.7");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "v6.0.9+bbb2222" };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var document = UniFiOsPlan();
        document.UniFiOsUpdate.TargetVersion = "v6.0.11+ccc3333";
        document.UniFiOsUpdate.Url = "https://example.test/os-6.0.11.bin";
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        return plan;
    }

    [Fact]
    public async Task APlannedBuildNewerThanTheConsolesOffer_IsInstalledByUrl()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedSharedBuildPlanAsync(harness);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.Calls.Should().Contain("ssh-unifi-os-update");
        harness.Commands.UniFiOsUpdateCalls.Should().Be(0, "the API would install the console's older 6.0.9");
        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.UniFiOsUpdate.Triggered.Should().BeTrue();
        stored.UniFiOsUpdate.TargetVersion.Should().Be("v6.0.11+ccc3333");
    }

    [Fact]
    public async Task APlannedBuildNewerThanTheConsolesOffer_FallsBackToTheConsoleBuildWhenSshFails()
    {
        using var harness = new RolloutHarness();
        harness.Commands.SshUniFiOsResult = FirmwareCommandResult.Failed("no route");
        var plan = await SeedSharedBuildPlanAsync(harness);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.Calls.Count(c => c == "ssh-unifi-os-update").Should().Be(1, "SSH is tried once, not again after the API");
        harness.Commands.UniFiOsUpdateCalls.Should().Be(1);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.TargetVersion.Should().Be("v6.0.9+bbb2222");
    }

    /// <summary>A plan whose UniFi OS target was chosen by hand: older than the console's own offer.</summary>
    private static async Task<FirmwareRolloutPlan> SeedPinnedOsPlanAsync(RolloutHarness harness)
    {
        harness.Commands.ConsoleInfo = Console(osChannel: "beta", installedOs: "6.0.7");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "v6.0.11+ccc3333" };
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var document = UniFiOsPlan();
        document.UniFiOsUpdate.TargetVersion = "6.0.9";
        document.UniFiOsUpdate.Url = "https://example.test/os-6.0.9.bin";
        document.UniFiOsUpdate.Pinned = true;
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        return plan;
    }

    [Fact]
    public async Task APinnedUniFiOsBuild_InstallsByUrlWhileTheConsoleOffersNewer()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedPinnedOsPlanAsync(harness);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.Calls.Should().Contain("ssh-unifi-os-update");
        harness.Commands.UniFiOsUpdateCalls.Should().Be(0, "the API would install the console's 6.0.11");
        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.UniFiOsUpdate.Triggered.Should().BeTrue();
        stored.UniFiOsUpdate.TargetVersion.Should().Be("6.0.9");
        harness.Bus.Published.Should().NotContain(e => e.EventType == RolloutAlerts.UniFiOsUpdateRefused);
    }

    [Fact]
    public async Task AnSshUniFiOsInstall_ShowsAsSendingWhileTheGatewayDownloads()
    {
        // The command returns only once the image has downloaded; the step must not read as queued meanwhile.
        using var harness = new RolloutHarness();
        var plan = await SeedPinnedOsPlanAsync(harness);
        DateTime? sendingDuringCommand = null;
        harness.Commands.DuringSshUniFiOsUpdate = async () =>
            sendingDuringCommand = Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.SendingAt;

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        sendingDuringCommand.Should().NotBeNull();
        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.UniFiOsUpdate.SendingAt.Should().BeNull("it clears once the command returns");
        stored.UniFiOsUpdate.Triggered.Should().BeTrue();
    }

    [Fact]
    public async Task AFailedSshUniFiOsInstall_ClearsSending()
    {
        using var harness = new RolloutHarness();
        harness.Commands.SshUniFiOsResult = FirmwareCommandResult.Failed("no route");
        var plan = await SeedPinnedOsPlanAsync(harness);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.SendingAt.Should().BeNull();
    }

    [Fact]
    public async Task APinnedUniFiOsBuild_IsNeverSwappedForTheConsolesBuildWhenSshFails()
    {
        using var harness = new RolloutHarness();
        harness.Commands.SshUniFiOsResult = FirmwareCommandResult.Failed("no route");
        var plan = await SeedPinnedOsPlanAsync(harness);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("refused");
    }

    [Fact]
    public async Task APinnedNetworkAppBuild_InstallsOverSshWhileTheConsoleOffersNewer()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(appChannel: "beta", appUpdateAvailable: "10.7.10", appVersion: "10.6.94");
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.Beta);
        var document = NetworkAppPlan();
        document.NetworkAppUpdate.TargetVersion = "10.7.2";
        document.NetworkAppUpdate.Url = "https://example.test/unifi/10.7.2/unifi-native_sysvinit.deb";
        document.NetworkAppUpdate.Pinned = true;
        var plan = await harness.SeedScheduledPlanAsync(document, RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Commands.Calls.Should().Contain("ssh-network-app-update");
        harness.Commands.NetworkAppUpdateCalls.Should().Be(0, "the API would install the console's 10.7.10");
        Stored((await harness.PlanAsync(plan.Id))!).NetworkAppUpdate.TargetVersion.Should().Be("10.7.2");
    }

    [Fact]
    public async Task APinnedDeviceImage_NeverFallsBackToTheConsolesOwnBuild()
    {
        using var harness = new RolloutHarness();
        harness.Commands.ExternalResult = FirmwareCommandResult.Failed("404");
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.TargetImages.Add(new PlanTargetImage { Mac = ApMac, Version = ToVersion, Url = "https://example.test/pinned.bin", Pinned = true });
        await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Commands.ExternalCommands.Should().NotBeEmpty()
            .And.OnlyContain(c => c.Item2 == "https://example.test/pinned.bin");
        harness.Commands.UpgradeCommands.Should().BeEmpty("the console's own upgrade installs its newest build");
    }

    [Fact]
    public async Task APinnedDeviceDowngrade_IsCommandedWithItsOwnImage()
    {
        using var harness = new RolloutHarness();
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.TargetImages.Add(new PlanTargetImage { Mac = ApMac, Version = FromVersion, Url = "https://example.test/older.bin", Pinned = true });
        var plan = await harness.SeedRunningPlanAsync(document, DowngradeStep());
        harness.Observer.Set(ApMac, Online, ToVersion);

        await harness.TickAsync();

        harness.Commands.ExternalCommands.Should().ContainSingle()
            .Which.Item2.Should().Be("https://example.test/older.bin");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task AnUnpinnedOlderTarget_IsStillRefused()
    {
        using var harness = new RolloutHarness();
        var document = Document(Wave(1, PlanStep(ApMac)));
        await harness.SeedRunningPlanAsync(document, DowngradeStep());
        harness.Observer.Set(ApMac, Online, ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromMinutes(2));

        harness.Commands.ExternalCommands.Should().BeEmpty();
        harness.Commands.UpgradeCommands.Should().BeEmpty();
    }

    /// <summary>A step from <see cref="ToVersion"/> back to <see cref="FromVersion"/>.</summary>
    private static FirmwareRolloutStep DowngradeStep()
    {
        var step = Step(ApMac, to: FromVersion);
        step.FromVersion = ToVersion;
        return step;
    }

    // --- The per-model channel a hand-added device build raised ----------------------------------

    private static readonly RolloutBuildPin DevicePin =
        new(FirmwareUrlKind.Device, "U6PRO", "7.0.12", "https://example.test/U6PRO-7.0.12.bin");

    [Fact]
    public void RaisedModelChannelsFor_RecordsEachEntryTheRolloutReplaced()
    {
        var family = DevicePin with { Models = ["U6PRO", "UAPA6A4"] };
        var raised = FirmwareRolloutService.RaisedModelChannelsFor(
            family, storedJson: "{\"USW24\":\"release\",\"UAPA6A4\":\"release\"}",
            newJson: "{\"USW24\":\"release\",\"U6PRO\":\"beta\",\"UAPA6A4\":\"beta\"}");

        raised.Should().HaveCount(2);
        raised.Single(r => r.Model == "U6PRO").Previous.Should().BeNull("the model had no entry of its own before");
        raised.Single(r => r.Model == "UAPA6A4").Previous.Should().Be("release");

        FirmwareRolloutService.RaisedModelChannelsFor(DevicePin, "{\"U6PRO\":\"beta\"}", "{\"U6PRO\":\"beta\"}")
            .Should().BeEmpty("nothing changed");
        FirmwareRolloutService.RaisedModelChannelsFor(
            DevicePin with { Kind = FirmwareUrlKind.UniFiOs }, "{}", "{\"U6PRO\":\"beta\"}")
            .Should().BeEmpty("only a device build raises a per-model channel");
    }

    private static async Task<FirmwareRolloutPlan> SeedRaisedModelPlanAsync(RolloutHarness harness, string? previous)
    {
        await harness.WithSettingsAsync(s => s.PerSkuChannelsJson = "{\"USW24\":\"release\",\"U6PRO\":\"beta\"}");
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.RaisedModelChannels = [new RaisedModelChannel { Model = "U6PRO", Channel = "beta", Previous = previous }];
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        return plan;
    }

    [Fact]
    public async Task AnAbortedRollout_PutsTheRaisedModelChannelBack()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedRaisedModelPlanAsync(harness, previous: null);

        await harness.Orchestrator.AbortAsync("the operator stopped it");

        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>((await harness.SettingsAsync()).PerSkuChannelsJson!)!;
        map.Should().NotContainKey("U6PRO", "it had no entry before the rollout");
        map.Should().Contain("USW24", "release", "other models are untouched");
        Stored((await harness.PlanAsync(plan.Id))!).RaisedModelChannels.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAbortedRollout_RestoresAPreviousModelEntry()
    {
        using var harness = new RolloutHarness();
        await SeedRaisedModelPlanAsync(harness, previous: "release-candidate");

        await harness.Orchestrator.AbortAsync("the operator stopped it");

        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>((await harness.SettingsAsync()).PerSkuChannelsJson!)!;
        map.Should().Contain("U6PRO", "release-candidate");
    }

    [Fact]
    public async Task AModelChannelChangedDuringTheRollout_IsLeftAlone()
    {
        using var harness = new RolloutHarness();
        await SeedRaisedModelPlanAsync(harness, previous: null);
        await harness.WithSettingsAsync(s => s.PerSkuChannelsJson = "{\"U6PRO\":\"release-candidate\"}");

        await harness.Orchestrator.AbortAsync("the operator stopped it");

        (await harness.SettingsAsync()).PerSkuChannelsJson.Should().Be("{\"U6PRO\":\"release-candidate\"}");
    }

    /// <summary>Walks one commanded device all the way through to its litmus verdict.</summary>
    private static async Task RunDeviceToLitmusAsync(RolloutHarness harness, string mac)
    {
        var existing = harness.Observer.Devices[mac];
        harness.Observer.Devices[mac] = existing with { State = (int)UniFiDeviceState.Disconnected };
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Observer.Devices[mac] = existing with { State = Online, Firmware = ToVersion, UpgradeToFirmware = null };
        await harness.TickAsync(TimeSpan.FromMinutes(4));
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        await harness.TickAsync(FirmwareRolloutOrchestrator.CoolDown);
    }

    [Fact]
    public async Task AConsoleUpdateBehindTheInstalledBuild_IsRefused()
    {
        // Live on atl-1365: the console ran 5.1.28 and, once GA was selected, offered 5.1.19 as its
        // pending update. Installing it would have rebooted the console onto an older UniFi OS.
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "release", installedOs: "5.1.28");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "5.1.19" };
        await harness.WithSettingsAsync(s => s.IncludeUniFiOs = true);
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        var stored = await harness.PlanAsync(plan.Id);
        Stored(stored!).UniFiOsUpdate.Outcome.Should().Be("nothing-to-update");
    }

    [Fact]
    public async Task AConsoleWhoseInstalledBuildIsUnknown_IsRefused()
    {
        // No installed version means no way to prove the offer is forward, and a console downgrade
        // is not recoverable from here - so the unknown case refuses rather than assuming.
        using var harness = new RolloutHarness();
        harness.Commands.ConsoleInfo = Console(osChannel: "release", installedOs: "");
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "5.1.19" };
        await harness.WithSettingsAsync(s => s.IncludeUniFiOs = true);
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
    }

}

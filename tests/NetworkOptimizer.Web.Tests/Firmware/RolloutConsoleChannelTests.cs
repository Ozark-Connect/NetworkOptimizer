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
}

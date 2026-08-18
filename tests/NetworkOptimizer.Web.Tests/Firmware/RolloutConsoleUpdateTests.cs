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
/// The two console-level updates that bracket a rollout: the UniFi Network application ahead of
/// wave 1, and the console's own UniFi OS after the last device.
///
/// Both take the API down with them, so both are persisted into PlanJson before the wait begins -
/// a server restart mid-update must never fire a second install. And both are deliberately
/// non-fatal in different ways: an application update that never comes back lets the device
/// upgrades go ahead regardless, while a console that never comes back is a Critical because
/// nothing else is going to notice.
/// </summary>
public class RolloutConsoleUpdateTests
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

    private static RolloutPlanDocument Stored(FirmwareRolloutPlan plan) =>
        JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson)!;

    // --- Wave 0: the UniFi Network application --------------------------------------------------

    [Fact]
    public async Task NetworkAppUpdate_IsInstalledBeforeAnyDeviceIsCommanded()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.ConsoleDark = true;

        await harness.TickAsync();

        harness.Commands.NetworkAppUpdateCalls.Should().Be(1);
        harness.Commands.UpgradeCommands.Should().BeEmpty();
        Stored((await harness.PlanAsync(plan.Id))!).NetworkAppUpdate.Triggered.Should().BeTrue();
    }

    [Fact]
    public async Task NetworkAppUpdate_ReleasesWave1WhenTheApplicationAnswersAgain()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.ConsoleDark = true;

        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromMinutes(2));
        harness.Commands.UpgradeCommands.Should().BeEmpty();

        // Back, and back on the new build: answering on the old version keeps the wait going.
        harness.Observer.ConsoleDark = false;
        harness.Commands.ConsoleInfo!.NetworkApplication!.Version = "9.1.0";
        await harness.TickAsync(TimeSpan.FromMinutes(1));

        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.NetworkAppUpdate.Settled.Should().BeTrue();
        stored.NetworkAppUpdate.Outcome.Should().Be("updated");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task NetworkAppUpdateNotComingBack_WarnsAndUpgradesTheDevicesAnyway()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.ConsoleDark = true;

        await harness.TickAsync();
        await harness.TickAsync(FirmwareRolloutOrchestrator.NetworkAppUpdateBudget + TimeSpan.FromMinutes(1));

        var alert = harness.Bus.Published.Single(e => e.EventType == RolloutAlerts.NetworkAppUpdateStuck);
        alert.Severity.Should().Be(AlertSeverity.Warning);
        Stored((await harness.PlanAsync(plan.Id))!).NetworkAppUpdate.Outcome.Should().Be("stuck");

        // The console answering again is a separate thing from the update having worked: the
        // devices roll either way.
        harness.Observer.ConsoleDark = false;
        await harness.TickAsync(TimeSpan.FromMinutes(1));
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task NothingToUpdate_GoesStraightToTheDevices()
    {
        using var harness = new RolloutHarness();
        harness.Commands.NetworkAppUpdateAccepted = false;
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        // The start pass commits the plan; the first device wave opens on the pass after it.
        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.NetworkAppUpdate.Settled.Should().BeTrue();
        stored.NetworkAppUpdate.Outcome.Should().Be("nothing-to-update");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
        harness.Bus.Published.Should().NotContain(e => e.EventType == RolloutAlerts.NetworkAppUpdateStuck);
    }

    [Fact]
    public async Task APlanThatDoesNotIncludeTheApplicationUpdate_NeverCommandsOne()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Commands.NetworkAppUpdateCalls.Should().Be(0);
        Stored((await harness.PlanAsync(plan.Id))!).NetworkAppUpdate.Outcome.Should().Be("skipped");
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task ARestartMidApplicationUpdate_DoesNotInstallItASecondTime()
    {
        using var harness = new RolloutHarness();
        var document = NetworkAppPlan();
        document.NetworkAppUpdate.Triggered = true;
        document.NetworkAppUpdate.TriggeredAt = RolloutHarness.Start;
        var plan = await harness.SeedRunningPlanAsync(document, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.ConsoleDark = true;

        await harness.TickAsync(TimeSpan.FromMinutes(1));

        harness.Commands.NetworkAppUpdateCalls.Should().Be(0);
        harness.Commands.UpgradeCommands.Should().BeEmpty();
    }

    // --- The final step: UniFi OS ---------------------------------------------------------------

    [Fact]
    public async Task UniFiOsUpdate_RunsOnlyAfterEveryDeviceStepHasSettled()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);

        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(1);
        var stored = Stored((await harness.PlanAsync(plan.Id))!);
        stored.UniFiOsUpdate.Triggered.Should().BeTrue();
        stored.UniFiOsUpdate.TargetVersion.Should().Be("4.3.6");
        // The rollout is not finished until the console is back.
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);
    }

    [Fact]
    public async Task UniFiOsUpdate_CompletesWhenTheConsoleReturnsAndStopsOfferingTheBuild()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        // Console dark through the cycle.
        harness.Commands.ConsoleInfo = null;
        await harness.TickAsync(TimeSpan.FromMinutes(5));
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);

        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Hardware = new UniFiConsoleHardware { FirmwareVersion = "4.3.6" },
        };
        harness.Commands.PendingUniFiOs = null;
        await harness.TickAsync(TimeSpan.FromMinutes(5));

        var stored = await harness.PlanAsync(plan.Id);
        stored!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
        Stored(stored).UniFiOsUpdate.Outcome.Should().Be("updated");
        harness.Bus.Published.Single(e => e.EventType == RolloutAlerts.Completed)
            .Message.Should().Contain("UniFi OS 4.3.6");
    }

    [Fact]
    public async Task AConsoleStillOfferingTheSameBuild_DidNotActuallyUpdate()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        // It came back, and the same build is still on offer: acceptance was not success.
        await harness.TickAsync(TimeSpan.FromMinutes(5));

        var stored = await harness.PlanAsync(plan.Id);
        Stored(stored!).UniFiOsUpdate.Outcome.Should().Be("unchanged");
        harness.Bus.Published.Single(e => e.EventType == RolloutAlerts.Completed)
            .Message.Should().Contain("still offering it");
    }

    [Fact]
    public async Task AnInstalledOsVersionMatchingTheTarget_IsSuccess_EvenIfABuildIsStillOffered()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "v4.3.6+abc123" };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        // A newer build published mid-cycle keeps the offer list non-empty; the installed
        // version is the authority.
        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Hardware = new UniFiConsoleHardware { FirmwareVersion = "4.3.6" },
        };
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "v4.3.7+def456" };
        await harness.TickAsync(TimeSpan.FromMinutes(5));

        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("updated");
    }

    [Fact]
    public async Task AnInstalledOsVersionStillOnTheOldBuild_IsUnchanged_EvenIfTheOfferCleared()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "v4.3.6+abc123" };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Hardware = new UniFiConsoleHardware { FirmwareVersion = "4.2.12" },
        };
        harness.Commands.PendingUniFiOs = null;
        await harness.TickAsync(TimeSpan.FromMinutes(5));

        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("unchanged");
    }

    [Fact]
    public async Task ADownloadingConsole_IsNotJudged_UntilTheUpdateStateGoesIdle()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        // Download and install run before the reboot: the console answers, the build is still
        // offered, and the old version is still installed. That must not read as "unchanged".
        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Hardware = new UniFiConsoleHardware { FirmwareVersion = "4.2.12" },
            Firmware = new UniFiConsoleFirmware
            {
                Update = new UniFiConsoleFirmwareUpdate { State = "DOWNLOADING" },
            },
        };
        await harness.TickAsync(TimeSpan.FromMinutes(10));
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Settled.Should().BeFalse();

        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Hardware = new UniFiConsoleHardware { FirmwareVersion = "4.3.6" },
        };
        await harness.TickAsync(TimeSpan.FromMinutes(5));
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("updated");
    }

    [Fact]
    public async Task AConsoleThatNeverComesBack_IsACriticalAgainstTheGateway()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        var document = UniFiOsPlan();
        var plan = await harness.SeedRunningPlanAsync(
            document,
            Step(GatewayMac, name: "Gateway", model: "UDMA6A8", deviceType: "ugw"));
        harness.Observer.Set(GatewayMac, Online, FromVersion, upgradeTo: ToVersion, model: "UDMA6A8", name: "Gateway");

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, GatewayMac);

        harness.Commands.ConsoleInfo = null;
        await harness.TickAsync(FirmwareRolloutOrchestrator.UniFiOsUpdateBudget + TimeSpan.FromMinutes(1));

        var alert = harness.Bus.Published.Single(e => e.EventType == RolloutAlerts.DeviceStuckOffline);
        alert.Severity.Should().Be(AlertSeverity.Critical);
        alert.DeviceId.Should().Be(GatewayMac);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("stuck");
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
    }

    [Fact]
    public async Task AStandaloneConsoleIsNeverCommandedEvenIfThePlanAsksForIt()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Firmware = new UniFiConsoleFirmware
            {
                Latest = new UniFiConsoleFirmwareRelease { Product = UniFiConsoleSystemInfo.StandaloneConsoleProduct },
            },
        };
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("refused");
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
    }

    [Fact]
    public async Task AConsoleWithNothingPending_FinishesWithoutCommandingAnything()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = null;
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.Outcome.Should().Be("nothing-to-update");
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
    }

    [Fact]
    public async Task ARestartMidConsoleUpdate_DoesNotInstallItASecondTime()
    {
        using var harness = new RolloutHarness();
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        var document = UniFiOsPlan();
        document.UniFiOsUpdate.Triggered = true;
        document.UniFiOsUpdate.TriggeredAt = RolloutHarness.Start;
        document.UniFiOsUpdate.TargetVersion = "4.3.6";
        var plan = await harness.SeedRunningPlanAsync(
            document, Step(ApMac, state: FirmwareRolloutStepState.LitmusPassed));
        harness.Commands.ConsoleInfo = null;

        await harness.TickAsync(TimeSpan.FromMinutes(1));

        harness.Commands.UniFiOsUpdateCalls.Should().Be(0);
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);
    }

    /// <summary>Walks one commanded device all the way through to its litmus verdict.</summary>
    private static async Task RunDeviceToLitmusAsync(RolloutHarness harness, string mac)
    {
        var existing = harness.Observer.Devices[mac];
        var isGateway = existing.Model is "UDMA6A8" or "UCGF" or "UDR" or "UDMPRO";
        harness.Observer.Devices[mac] = existing with { State = (int)UniFiDeviceState.Disconnected };
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Observer.Devices[mac] = existing with { State = Online, Firmware = ToVersion, UpgradeToFirmware = null };
        await harness.TickAsync(TimeSpan.FromMinutes(4));
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        var cooldown = isGateway ? FirmwareRolloutOrchestrator.GatewayCoolDown : FirmwareRolloutOrchestrator.CoolDown;
        await harness.TickAsync(cooldown);
    }
}

/// <summary>
/// The console backup response's mapping onto a rollout result. The message is what the postpone
/// alert shows the operator, so a partial failure has to name the components that failed rather
/// than saying the backup failed and leaving them to guess.
/// </summary>
public class BackupResultMappingTests
{
    private static UniFiConsoleBackupComponent Component(bool success) => new() { Success = success };

    [Fact]
    public void ANullResponse_IsAFailure()
    {
        var result = FirmwareCommandClient.MapBackupResult(null);

        result.Outcome.Should().Be(FirmwareCommandOutcome.Failed);
        result.Message.Should().Contain("did not answer");
    }

    [Fact]
    public void ASuccessfulBackup_IsOk()
    {
        var result = FirmwareCommandClient.MapBackupResult(new UniFiConsoleBackupResult { Success = true });

        result.IsOk.Should().BeTrue();
    }

    [Fact]
    public void APartialFailure_NamesEveryComponentThatFailed()
    {
        var result = FirmwareCommandClient.MapBackupResult(new UniFiConsoleBackupResult
        {
            Success = false,
            Controllers = { ["network"] = Component(true), ["protect"] = Component(false) },
            Services = { ["users"] = Component(false) },
        });

        result.Outcome.Should().Be(FirmwareCommandOutcome.Failed);
        result.Message.Should().Contain("protect").And.Contain("users");
        result.Message.Should().NotContain("network");
    }

    [Fact]
    public void AFailureWithNoNamedComponents_StillSaysItFailed()
    {
        var result = FirmwareCommandClient.MapBackupResult(new UniFiConsoleBackupResult { Success = false });

        result.Outcome.Should().Be(FirmwareCommandOutcome.Failed);
        result.Message.Should().Contain("unsuccessful");
    }

}

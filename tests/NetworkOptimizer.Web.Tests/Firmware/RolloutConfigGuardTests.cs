using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;
using static NetworkOptimizer.Web.Tests.Firmware.RolloutFixtures;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>Change Control's hooks into a rollout: the pin before it starts and the post-upgrade config hold.</summary>
public class RolloutConfigGuardTests
{
    private const int Online = (int)UniFiDeviceState.Connected;

    private static RolloutPlanDocument NetworkAppPlan()
    {
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiNetworkUpdate = true;
        return document;
    }

    private static RolloutPlanDocument Stored(FirmwareRolloutPlan plan) =>
        JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson)!;

    private static async Task<FirmwareRolloutPlan> RunToNetworkAppSettledAsync(RolloutHarness harness)
    {
        var plan = await harness.SeedScheduledPlanAsync(NetworkAppPlan(), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.ConsoleDark = true;
        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromMinutes(2));
        harness.Observer.ConsoleDark = false;
        harness.Commands.ConsoleInfo!.NetworkApplication!.Version = "9.1.0";
        await harness.TickAsync(TimeSpan.FromMinutes(1));
        return plan;
    }

    [Fact]
    public async Task Start_PinsASnapshotAndRecordsIt()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(Document(Wave(1, PlanStep(ApMac))), RolloutHarness.Start, Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.ConfigGuard.Pins.Should().Be(1);
        Stored((await harness.PlanAsync(plan.Id))!).ConfigSnapshotId.Should().Be(42);
    }

    [Fact]
    public async Task CleanConfigAfterNetworkApp_ReleasesWave1()
    {
        using var harness = new RolloutHarness();
        var plan = await RunToNetworkAppSettledAsync(harness);

        harness.ConfigGuard.Checks.Should().Be(1);
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }

    [Fact]
    public async Task RegressionAfterNetworkApp_PausesBeforeAnyDevice()
    {
        using var harness = new RolloutHarness();
        harness.ConfigGuard.HoldReason = "1 security regression";
        var plan = await RunToNetworkAppSettledAsync(harness);

        var stored = (await harness.PlanAsync(plan.Id))!;
        stored.Status.Should().Be(FirmwareRolloutStatus.Paused);
        Stored(stored).Notes.Should().Contain(n => n.Contains("1 security regression"));
        harness.Commands.UpgradeCommands.Should().BeEmpty();
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.ConfigHeld);
    }

    [Fact]
    public async Task ResumeAfterHold_DoesNotCheckAgain()
    {
        using var harness = new RolloutHarness();
        harness.ConfigGuard.HoldReason = "1 security regression";
        var plan = await RunToNetworkAppSettledAsync(harness);

        await harness.Orchestrator.ResumeAsync();
        await harness.TickAsync(TimeSpan.FromSeconds(10));

        harness.ConfigGuard.Checks.Should().Be(1);
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Commanded);
    }
}

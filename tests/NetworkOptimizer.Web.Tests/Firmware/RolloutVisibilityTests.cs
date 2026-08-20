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
/// Deadlines are measured in time the rollout could actually see the site. A dark console, a
/// dropped agent tunnel or a server that was not running is our own vantage failing, and says
/// nothing about the device it would otherwise condemn - so the run stalls instead of failing it.
///
/// The line is drawn at "no observations at all": a device missing from a console that answered is
/// news about that device, not blindness, and its budget keeps running.
/// </summary>
public class RolloutVisibilityTests
{
    private const int Online = (int)UniFiDeviceState.Connected;
    private const int Offline = (int)UniFiDeviceState.Disconnected;

    private static RolloutVisibility Visibility(FirmwareRolloutPlan plan) =>
        JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson)!.Visibility;

    /// <summary>Commands a device, then takes it offline so a stuck-offline budget is running.</summary>
    private static async Task<FirmwareRolloutPlan> SeedDownDeviceAsync(RolloutHarness harness)
    {
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac, budgetSeconds: 900))), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Down);
        return plan;
    }

    [Fact]
    public async Task ADeviceDownWhileWeAreBlind_IsNotDeclaredStuck()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedDownDeviceAsync(harness);

        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(TimeSpan.FromMinutes(20));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Down);
        harness.Bus.Published.Should().NotContain(e => e.EventType == RolloutAlerts.DeviceStuckOffline);
    }

    [Fact]
    public async Task TheSameDeviceIsDeclaredStuck_OnceThatMuchWatchedTimePasses()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedDownDeviceAsync(harness);

        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(TimeSpan.FromMinutes(20));

        // The console answers again and the device is still offline: from here the budget is real
        // time, watched, and it runs out on its own terms.
        harness.Observer.ConsoleDark = false;
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Down);

        await harness.TickAsync(TimeSpan.FromMinutes(16));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
        harness.Bus.Published.Should().Contain(e =>
            e.EventType == RolloutAlerts.DeviceStuckOffline && e.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public async Task ACommandedDevice_IsNotEscalatedOverBlindTime()
    {
        using var harness = new RolloutHarness();
        harness.Commands.Catalog.Add(new UniFiFirmwareCatalogEntry
        {
            BaseModel = "U6PRO",
            Version = ToVersion,
            Url = "https://fw-download.example.net/u6pro.bin",
        });
        await harness.SeedRunningPlanAsync(Document(Wave(1, PlanStep(ApMac))), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();

        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromMinutes(2));

        // Back, and the device has not moved. The grace window has not been watched yet, so this
        // is not the device ignoring its command.
        harness.Observer.ConsoleDark = false;
        await harness.TickAsync(TimeSpan.FromSeconds(20));
        harness.Commands.SshCommands.Should().BeEmpty();

        await harness.TickAsync(FirmwareRolloutOrchestrator.CommandGraceWindow + TimeSpan.FromSeconds(10));
        harness.Commands.SshCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task TimeAServerWasNotRunning_IsNotChargedToTheDeviceMidCycle()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedDownDeviceAsync(harness);

        // No passes at all for half an hour, then a fresh executor: exactly what a restart leaves.
        harness.Time.Advance(TimeSpan.FromMinutes(30));
        var restarted = harness.NewOrchestrator();
        await restarted.TickAsync();

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Down);
        harness.Bus.Published.Should().NotContain(e => e.EventType == RolloutAlerts.DeviceStuckOffline);

        harness.Time.Advance(TimeSpan.FromMinutes(16));
        await restarted.TickAsync();

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
    }

    [Fact]
    public async Task BlindTimeIsRecordedOnThePlan_SoItSurvivesARestart()
    {
        using var harness = new RolloutHarness();
        var plan = await SeedDownDeviceAsync(harness);

        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(TimeSpan.FromMinutes(10));
        harness.Observer.ConsoleDark = false;
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        var visibility = Visibility((await harness.PlanAsync(plan.Id))!);
        visibility.BlindSince.Should().BeNull("the spell ended");
        visibility.LastTickAt.Should().Be(harness.Time.GetUtcNow().UtcDateTime);
        visibility.Blind.Should().ContainSingle()
            .Which.Should().Match<RolloutBlindInterval>(b => (b.To - b.From) >= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task ADeviceMissingFromAConsoleThatAnswered_IsRealNews()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(
            Document(Wave(1, PlanStep(ApMac, budgetSeconds: 900), PlanStep(PeerMac))),
            Step(ApMac),
            Step(PeerMac, name: "AP 2"));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.Set(PeerMac, Online, FromVersion, upgradeTo: ToVersion, name: "AP 2");

        await harness.TickAsync();
        harness.Observer.Set(ApMac, Offline, FromVersion);
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        // The console still answers - for everything except this device. That is the device gone,
        // not the site out of sight, so its budget keeps running.
        harness.Observer.Devices.Remove(ApMac);
        await harness.TickAsync(TimeSpan.FromMinutes(16));

        (await harness.StepAsync(plan.Id, ApMac)).State.Should().Be(FirmwareRolloutStepState.Failed);
        harness.Bus.Published.Should().Contain(e => e.EventType == RolloutAlerts.DeviceStuckOffline);
        Visibility((await harness.PlanAsync(plan.Id))!).Blind.Should().BeEmpty();
    }

    [Fact]
    public async Task LosingSightOfTheSite_IsAnnouncedOnceAndTakenBackWhenItReturns()
    {
        using var harness = new RolloutHarness();
        await SeedDownDeviceAsync(harness);

        harness.Observer.ConsoleDark = true;
        await harness.TickAsync(TimeSpan.FromMinutes(2));
        harness.Bus.Published.Should().NotContain(e => e.EventType == RolloutAlerts.VisibilityLost,
            "a short gap is a device rebooting, not a rollout in trouble");

        await harness.TickAsync(FirmwareRolloutOrchestrator.VisibilityLostAfter);
        await harness.TickAsync(TimeSpan.FromMinutes(5));

        harness.Bus.Published.Should().ContainSingle(e => e.EventType == RolloutAlerts.VisibilityLost)
            .Which.Severity.Should().Be(AlertSeverity.Warning);

        harness.Observer.ConsoleDark = false;
        await harness.TickAsync(TimeSpan.FromSeconds(20));

        harness.Bus.Published.Should().ContainSingle(e => e.EventType == RolloutAlerts.VisibilityRestored)
            .Which.Severity.Should().Be(AlertSeverity.Info);
        harness.Bus.Published.Where(e => e.Source == RolloutAlerts.Source)
            .Should().OnlyContain(e => e.SourceUrl == "/firmware-rollout");
    }

    [Fact]
    public async Task NothingIsCommandedIntoADarkConsole()
    {
        using var harness = new RolloutHarness();
        await harness.SeedRunningPlanAsync(Document(Wave(1, PlanStep(ApMac))), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);
        harness.Observer.ConsoleDark = true;

        await harness.TickAsync();
        await harness.TickAsync(TimeSpan.FromMinutes(10));

        harness.Commands.UpgradeCommands.Should().BeEmpty();
        harness.Commands.SshCommands.Should().BeEmpty();
    }
}

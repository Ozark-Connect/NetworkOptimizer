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
/// The console's own before/after numbers. A UCG's device firmware IS UniFi OS, so the console
/// earns a report row like any other device - but its measurements come off the console step
/// rather than a device step, which is where every gap below lived.
/// </summary>
public class RolloutGatewayReportTests
{
    private const int Online = (int)UniFiDeviceState.Connected;

    private static RolloutPlanDocument UniFiOsPlan()
    {
        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiOsUpdate = true;
        document.ConsoleMac = GatewayMac;
        return document;
    }

    private static RolloutPlanDocument Stored(FirmwareRolloutPlan plan) =>
        JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson)!;

    private static UniFiConsoleSystemInfo ConsoleOn(string version) => new()
    {
        Hardware = new UniFiConsoleHardware { FirmwareVersion = version },
    };

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

    /// <summary>Trigger the OS update and leave the console back up on its new build.</summary>
    private static async Task<FirmwareRolloutPlan> RunOsUpdateAsync(RolloutHarness harness)
    {
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease { Version = "4.3.6" };
        harness.Commands.ConsoleInfo = ConsoleOn("4.3.5");
        var plan = await harness.SeedRunningPlanAsync(UniFiOsPlan(), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);
        return plan;
    }

    [Fact]
    public async Task PreStats_AreCapturedBeforeTheConsoleIsCommanded()
    {
        using var harness = new RolloutHarness();
        harness.Litmus.StatsByMac[GatewayMac] =
            new RolloutResourceStats { CpuPercent = 30, MemoryUsedPercent = 70, SampleCount = 40 };

        var plan = await RunOsUpdateAsync(harness);

        var os = Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate;
        os.Triggered.Should().BeTrue();
        os.PreStatsJson.Should().NotBeNull();
        JsonSerializer.Deserialize<RolloutResourceStats>(os.PreStatsJson!)!.CpuPercent.Should().Be(30);
    }

    [Fact]
    public async Task ATransientApiFailureDuringTheDownload_DoesNotStartTheDowntimeClock()
    {
        using var harness = new RolloutHarness();
        var plan = await RunOsUpdateAsync(harness);

        // One failed poll while the console is still up and downloading.
        harness.Commands.ConsoleInfo = null;
        await harness.TickAsync(TimeSpan.FromSeconds(30));
        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.WentDownAt.Should().NotBeNull();

        // It answers again on the OLD version, so the reboot is still ahead of us.
        harness.Commands.ConsoleInfo = ConsoleOn("4.3.5");
        await harness.TickAsync(TimeSpan.FromSeconds(30));

        Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate.WentDownAt.Should().BeNull();
    }

    [Fact]
    public async Task DowntimeIsMeasuredFromTheRealDarkStretch_NotTheJudgeDelay()
    {
        using var harness = new RolloutHarness();
        var plan = await RunOsUpdateAsync(harness);

        harness.Commands.ConsoleInfo = null;
        await harness.TickAsync(TimeSpan.FromSeconds(10));

        // Back on the new build well inside the 2 minute judge delay.
        harness.Commands.ConsoleInfo = ConsoleOn("4.3.6");
        harness.Commands.PendingUniFiOs = null;
        await harness.TickAsync(TimeSpan.FromSeconds(40));

        var os = Stored((await harness.PlanAsync(plan.Id))!).UniFiOsUpdate;
        os.WentDownAt.Should().NotBeNull();
        os.BackAt.Should().NotBeNull();

        // Observed, not judged: roughly the 40s it was actually away, nowhere near the delay.
        (os.BackAt!.Value - os.WentDownAt!.Value).Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task TheReportWaitsForTheConsoleWindow_AndCarriesTheGatewayRow()
    {
        using var harness = new RolloutHarness();
        harness.Litmus.StatsByMac[GatewayMac] =
            new RolloutResourceStats { CpuPercent = 30, MemoryUsedPercent = 70, SampleCount = 40 };

        var plan = await RunOsUpdateAsync(harness);

        harness.Commands.ConsoleInfo = null;
        await harness.TickAsync(TimeSpan.FromMinutes(3));
        harness.Commands.ConsoleInfo = ConsoleOn("4.3.6");
        harness.Commands.PendingUniFiOs = null;
        await harness.TickAsync(TimeSpan.FromMinutes(1));

        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);

        // The device's own window closes first. The report must NOT freeze here: the console's
        // window opens a gateway cool-down after its own trigger, which is later in every path.
        await harness.TickAsync(TimeSpan.FromHours(2));
        var midSoak = await harness.PlanAsync(plan.Id);
        midSoak!.ReportJson.Should().BeNull("the console's after-window has not opened yet");

        harness.Litmus.StatsByMac[GatewayMac] =
            new RolloutResourceStats { CpuPercent = 18, MemoryUsedPercent = 62, SampleCount = 40 };
        await harness.TickAsync(TimeSpan.FromHours(3));

        var done = await harness.PlanAsync(plan.Id);
        done!.ReportJson.Should().NotBeNull();

        var report = RolloutReport.Parse(done.ReportJson)!;
        var gateway = report.Rows.Single(r => string.Equals(r.Mac, GatewayMac, StringComparison.OrdinalIgnoreCase));
        gateway.CpuBeforeMean.Should().Be(30);
        gateway.CpuAfterMean.Should().Be(18);
        gateway.MemBeforeMean.Should().Be(70);
        gateway.MemAfterMean.Should().Be(62);
        gateway.DowntimeSeconds.Should().NotBeNull();
        gateway.Outcome.Should().Be(RolloutOutcomes.Upgraded);
    }

    [Fact]
    public async Task AStuckConsoleDoesNotHoldTheReportForever()
    {
        using var harness = new RolloutHarness();
        var plan = await RunOsUpdateAsync(harness);

        harness.Commands.ConsoleInfo = null;
        await harness.TickAsync(TimeSpan.FromMinutes(3));
        harness.Commands.ConsoleInfo = ConsoleOn("4.3.6");
        harness.Commands.PendingUniFiOs = null;
        await harness.TickAsync(TimeSpan.FromMinutes(1));

        // Far past the console's own window: a capture that never lands stops blocking.
        await harness.TickAsync(TimeSpan.FromHours(12));

        (await harness.PlanAsync(plan.Id))!.ReportJson.Should().NotBeNull();
    }

    [Fact]
    public async Task ARolloutWithNoConsoleUpdate_GetsNoGatewayRow()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedRunningPlanAsync(Document(Wave(1, PlanStep(ApMac))), Step(ApMac));
        harness.Observer.Set(ApMac, Online, FromVersion, upgradeTo: ToVersion);

        await harness.TickAsync();
        await RunDeviceToLitmusAsync(harness, ApMac);
        await harness.TickAsync(TimeSpan.FromHours(3));

        var done = await harness.PlanAsync(plan.Id);
        done!.ReportJson.Should().NotBeNull();
        RolloutReport.Parse(done.ReportJson)!.Rows
            .Should().NotContain(r => string.Equals(r.Mac, GatewayMac, StringComparison.OrdinalIgnoreCase));
    }
}

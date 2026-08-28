using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The target version a plan commits a device to, and which channel it belongs to. The catalog
/// settles it, because a device record can still name the channel before this one.
/// </summary>
public class RolloutChannelTargetTests
{
    private const string SwitchMac = "aa:bb:cc:dd:ee:03";
    private const string SwitchModel = "USWED76";
    private const string Installed = "7.5.10";

    private static PlannerDevice Switch(string toVersion, bool upgradable = true) => new()
    {
        Mac = SwitchMac,
        Name = "Switch 1",
        Model = SwitchModel,
        DisplayModel = "USW-Pro-XG-8-PoE",
        Type = DeviceType.Switch,
        Upgradable = upgradable,
        FromVersion = Installed,
        ToVersion = toVersion,
        IpAddress = "192.0.2.20",
    };

    private static UniFiFirmwareCatalogEntry Entry(string version) => new()
    {
        BaseModel = SwitchModel,
        Device = SwitchModel,
        Version = version,
        Url = $"https://example.invalid/{SwitchModel}-{version}.bin",
    };

    /// <summary>A site on autopilot pinned to release-candidate, with its console already there.</summary>
    private static async Task<RolloutHarness> SiteOnReleaseCandidateAsync(PlannerDevice device)
    {
        var harness = new RolloutHarness();
        harness.Planning.Devices.Add(device);
        harness.Commands.DeviceChannel = FirmwareChannels.ReleaseCandidate;
        await harness.WithSettingsAsync(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.GlobalChannel = FirmwareChannels.ReleaseCandidate;
            s.IncludeUniFiNetwork = false;
            s.IncludeUniFiOs = false;
        });
        return harness;
    }

    private static RolloutPlanDocument Stored(FirmwareRolloutPlan plan) =>
        JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson)!;

    [Fact]
    public async Task StaleTargetFromAnotherChannel_IsNotPlanned()
    {
        // The console still offers the Early Access build it derived before the channel moved.
        using var harness = await SiteOnReleaseCandidateAsync(Switch(toVersion: "7.5.15.17146"));
        harness.Commands.CatalogByChannel[FirmwareChannels.ReleaseCandidate] = [Entry("7.5.10.17129")];

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        planId.Should().BeNull(
            "release-candidate carries only the build the switch already runs, so there is nothing to do");
    }

    [Fact]
    public async Task TargetIsTakenFromTheChannelCatalog_NotTheDeviceRecord()
    {
        using var harness = await SiteOnReleaseCandidateAsync(Switch(toVersion: "7.5.15.17146"));
        harness.Commands.CatalogByChannel[FirmwareChannels.ReleaseCandidate] = [Entry("7.6.2.17300")];

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        var document = Stored((await harness.PlanAsync(planId!.Value))!);
        var step = document.Waves.SelectMany(w => w.Steps).Should().ContainSingle().Subject;
        step.ToVersion.Should().Be("7.6.2.17300");
        document.TargetImages.Should().ContainSingle()
            .Which.Url.Should().Contain("7.6.2.17300", "the image has to be the build the step names");
    }

    [Fact]
    public async Task ModelTheChannelDoesNotCarry_IsDropped()
    {
        using var harness = await SiteOnReleaseCandidateAsync(Switch(toVersion: "7.5.15.17146"));
        harness.Commands.CatalogByChannel[FirmwareChannels.ReleaseCandidate] =
            [new UniFiFirmwareCatalogEntry { BaseModel = "OTHER", Device = "OTHER", Version = "1.0.0", Url = "https://example.invalid/x.bin" }];

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        planId.Should().BeNull("a build this channel does not carry cannot be commanded on it");
    }

    [Fact]
    public async Task EmptyCatalog_DropsNothing()
    {
        // The console answering with nothing is this app failing to read it, not a channel that
        // offers nothing. Emptying the plan on it would hide every real update behind one bad read.
        using var harness = await SiteOnReleaseCandidateAsync(Switch(toVersion: "7.9.9.99999"));
        harness.Commands.CatalogByChannel[FirmwareChannels.ReleaseCandidate] = [];

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        planId.Should().NotBeNull();
    }

    [Fact]
    public async Task PlanningAsksTheConsoleToReDeriveDeviceTargets_BeforeReadingTheCatalog()
    {
        using var harness = await SiteOnReleaseCandidateAsync(Switch(toVersion: "7.6.2.17300"));
        harness.Commands.CatalogByChannel[FirmwareChannels.ReleaseCandidate] = [Entry("7.6.2.17300")];

        await harness.Autopilot.CreatePlanIfDueAsync();

        harness.Commands.DeviceFirmwareChecks.Should().BeGreaterThan(0);
        harness.Commands.Calls.IndexOf("device-firmware-check")
            .Should().BeLessThan(harness.Commands.Calls.IndexOf("list-available"));
    }
}

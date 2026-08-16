using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The settings row is a working copy - the executor reads it live, so committing any rollout
/// writes it - which left Autopilot's own configuration with nowhere to live. These cover the
/// separation: a one-off cannot become the standing config, and the standing config is what
/// Autopilot both plans and runs from.
/// </summary>
public class AutopilotStandingConfigTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:01";

    private static PlannerDevice Ap(string mac = ApMac) => new()
    {
        Mac = mac,
        Name = "AP 1",
        Model = "SKU-AP1",
        DisplayModel = "SKU-AP1",
        Type = DeviceType.AccessPoint,
        Upgradable = true,
        FromVersion = "1.0.0",
        ToVersion = "1.1.0",
        IpAddress = "192.0.2.10",
    };

    private static async Task<RolloutHarness> AutopilotSiteAsync()
    {
        var harness = new RolloutHarness();
        harness.Planning.Devices.Add(Ap());
        await harness.WithSettingsAsync(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.GlobalChannel = FirmwareChannels.Beta;
            s.IncludeUniFiNetwork = false;
            s.IncludeUniFiOs = false;
        });
        return harness;
    }

    // ---- The snapshot round trip ------------------------------------------------------------

    [Fact]
    public void ACaptureDoesNotNestThePreviousOne()
    {
        var settings = new FirmwareRolloutSettings { GlobalChannel = FirmwareChannels.Beta };

        settings.AutopilotSettingsJson = AutopilotSettingsSnapshot.Serialize(settings);
        var second = AutopilotSettingsSnapshot.Serialize(settings);

        second.Should().NotContain("AutopilotSettingsJson");
        AutopilotSettingsSnapshot.Deserialize(second)!.GlobalChannel.Should().Be(FirmwareChannels.Beta);
    }

    [Fact]
    public void AnUnreadableOrAbsentCaptureIsNull()
    {
        AutopilotSettingsSnapshot.Deserialize(null).Should().BeNull();
        AutopilotSettingsSnapshot.Deserialize("   ").Should().BeNull();
        AutopilotSettingsSnapshot.Deserialize("{ not json").Should().BeNull();
    }

    // ---- Who may write it -------------------------------------------------------------------

    [Fact]
    public async Task APlainSettingsSave_NeverCapturesTheStandingConfig()
    {
        using var harness = new RolloutHarness();
        var settings = await harness.Repository.GetSettingsAsync();
        settings.Mode = FirmwareRolloutMode.Autopilot;

        await harness.Repository.SaveSettingsAsync(settings);

        (await harness.Repository.GetSettingsAsync()).AutopilotSettingsJson.Should().BeNull();
    }

    [Fact]
    public async Task TheSnapshotWriterTouchesNothingElseOnTheRow()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.GlobalChannel = FirmwareChannels.ReleaseCandidate);

        await harness.Repository.SaveAutopilotSnapshotAsync("""{"GlobalChannel":"beta"}""");

        var stored = await harness.Repository.GetSettingsAsync();
        stored.GlobalChannel.Should().Be(FirmwareChannels.ReleaseCandidate);
        stored.AutopilotSettingsJson.Should().Be("""{"GlobalChannel":"beta"}""");
    }

    // ---- Seeding ----------------------------------------------------------------------------

    [Fact]
    public async Task AnUpgradedSiteCapturesItsExistingSettingsOnTheFirstCheck()
    {
        using var harness = await AutopilotSiteAsync();
        (await harness.Repository.GetSettingsAsync()).AutopilotSettingsJson.Should().BeNull();

        await harness.Autopilot.CreatePlanIfDueAsync();

        var captured = AutopilotSettingsSnapshot.Deserialize(
            (await harness.Repository.GetSettingsAsync()).AutopilotSettingsJson);
        captured.Should().NotBeNull();
        captured!.GlobalChannel.Should().Be(FirmwareChannels.Beta);
    }

    [Fact]
    public async Task ASiteThatIsNotOnAutopilot_CapturesNothing()
    {
        using var harness = new RolloutHarness();
        harness.Planning.Devices.Add(Ap());
        await harness.WithSettingsAsync(s => s.Mode = FirmwareRolloutMode.ManualOnly);

        await harness.Autopilot.CreatePlanIfDueAsync();

        (await harness.Repository.GetSettingsAsync()).AutopilotSettingsJson.Should().BeNull();
    }

    // ---- The point of the whole thing --------------------------------------------------------

    [Fact]
    public async Task AutopilotPlansFromItsOwnConfig_NotWhatAOneOffLeftBehind()
    {
        using var harness = await AutopilotSiteAsync();

        // Capture beta as the standing config, then let a one-off overwrite the row with GA.
        await harness.Repository.SaveAutopilotSnapshotAsync(
            AutopilotSettingsSnapshot.Serialize(await harness.Repository.GetSettingsAsync()));
        var oneOff = await harness.Repository.GetSettingsAsync();
        oneOff.GlobalChannel = FirmwareChannels.Release;
        await harness.Repository.SaveSettingsAsync(oneOff);

        await harness.Autopilot.CreatePlanIfDueAsync();

        // I2: what it planned from is what the executor will read live.
        (await harness.Repository.GetSettingsAsync()).GlobalChannel.Should().Be(FirmwareChannels.Beta);
    }

    [Fact]
    public async Task CommittingAOneOff_LeavesTheModeAlone()
    {
        using var harness = await AutopilotSiteAsync();

        var working = await harness.Repository.GetSettingsAsync();
        working.Mode = FirmwareRolloutMode.ManualOnly;
        await harness.Service.SchedulePlanAsync(working, DateTime.UtcNow.AddHours(6));

        (await harness.Repository.GetSettingsAsync()).Mode.Should().Be(FirmwareRolloutMode.Autopilot);
    }

    [Fact]
    public async Task CommittingAOneOff_NeverCapturesItsScope()
    {
        using var harness = await AutopilotSiteAsync();
        await harness.Repository.SaveAutopilotSnapshotAsync(
            AutopilotSettingsSnapshot.Serialize(await harness.Repository.GetSettingsAsync()));

        var working = await harness.Repository.GetSettingsAsync();
        working.GlobalChannel = FirmwareChannels.Release;
        await harness.Service.SchedulePlanAsync(working, DateTime.UtcNow.AddHours(6));

        var captured = AutopilotSettingsSnapshot.Deserialize(
            (await harness.Repository.GetSettingsAsync()).AutopilotSettingsJson);
        captured!.GlobalChannel.Should().Be(FirmwareChannels.Beta);
    }

    // ---- Off and back on ---------------------------------------------------------------------

    [Fact]
    public async Task TurningAutopilotOffKeepsItsConfig_AndReEnablingRestoresIt()
    {
        using var harness = await AutopilotSiteAsync();
        await harness.Service.SaveAutopilotSettingsAsync(await harness.Repository.GetSettingsAsync());

        await harness.Service.DisableAutopilotAsync();

        var off = await harness.Repository.GetSettingsAsync();
        off.Mode.Should().Be(FirmwareRolloutMode.ManualOnly);
        off.AutopilotSettingsJson.Should().NotBeNull();

        // A one-off while it is off must not survive the restore.
        off.GlobalChannel = FirmwareChannels.Release;
        await harness.Repository.SaveSettingsAsync(off);

        (await harness.Service.ReEnableAutopilotAsync()).Should().BeTrue();

        var back = await harness.Repository.GetSettingsAsync();
        back.Mode.Should().Be(FirmwareRolloutMode.Autopilot);
        back.GlobalChannel.Should().Be(FirmwareChannels.Beta);
    }

    [Fact]
    public async Task ReEnablingWithNothingCaptured_ReportsThereIsNothingToRestore()
    {
        using var harness = new RolloutHarness();

        (await harness.Service.ReEnableAutopilotAsync()).Should().BeFalse();
        (await harness.Repository.GetSettingsAsync()).Mode.Should().NotBe(FirmwareRolloutMode.Autopilot);
    }

    [Fact]
    public async Task SavingAutopilotSettings_CapturesThemAndTurnsItOn()
    {
        using var harness = new RolloutHarness();
        var settings = await harness.Repository.GetSettingsAsync();
        settings.GlobalChannel = FirmwareChannels.ReleaseCandidate;

        await harness.Service.SaveAutopilotSettingsAsync(settings);

        var stored = await harness.Repository.GetSettingsAsync();
        stored.Mode.Should().Be(FirmwareRolloutMode.Autopilot);
        AutopilotSettingsSnapshot.Deserialize(stored.AutopilotSettingsJson)!
            .GlobalChannel.Should().Be(FirmwareChannels.ReleaseCandidate);
    }
}

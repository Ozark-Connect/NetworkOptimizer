using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.DeviceRestart;

/// <summary>
/// A restart Network Optimizer sent becomes the detail of the boot's one reboot record, never a
/// second mark: who asked and how, over whatever the probe read.
/// </summary>
public class PlannedRestartReasonTests
{
    private static readonly DateTime At = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

    private static PlannedRestart Restart(bool lostPowerOnly = false) =>
        new(RebootCategory.CommandedReboot, "Restarted by Network Optimizer, for admin", At, At.AddMinutes(12), lostPowerOnly);

    private static readonly DeviceRebootReason Clean =
        new(RebootCategory.CommandedReboot, "Restarted", "Clean shutdown", RebootReasonSource.PstoreConsole);

    private static readonly DeviceRebootReason PowerLoss =
        new(RebootCategory.PowerLoss, "Power loss", "No shutdown recorded", RebootReasonSource.PstoreCleared);

    [Fact]
    public void CleanRestart_TakesOurDetail()
    {
        var reason = DeviceRebootTracker.ApplyPlannedRestart(Clean, Restart())!;

        reason.Category.Should().Be(RebootCategory.CommandedReboot);
        reason.Detail.Should().Be("Restarted by Network Optimizer, for admin");
        reason.Source.Should().Be(RebootReasonSource.PstoreConsole);
    }

    [Fact]
    public void UncleanRestart_KeepsWhatTheProbeSaw()
    {
        var reason = DeviceRebootTracker.ApplyPlannedRestart(PowerLoss, Restart())!;

        reason.Category.Should().Be(RebootCategory.CommandedReboot);
        reason.Detail.Should().Be("Restarted by Network Optimizer, for admin (shutdown was not clean: power loss)");
        reason.IsUnexpected.Should().BeFalse();
    }

    [Fact]
    public void PoeCycle_IsAPowerCycle_NotAPowerLoss()
    {
        var planned = new PlannedRestart(RebootCategory.PowerCycle,
            "Power-cycled by Network Optimizer, for admin (PoE port 7 on Office Switch)", At, At.AddMinutes(12));

        var reason = DeviceRebootTracker.ApplyPlannedRestart(PowerLoss, planned)!;

        reason.Category.Should().Be(RebootCategory.PowerCycle);
        reason.Summary.Should().Be("Power cycled");
        reason.IsUnexpected.Should().BeFalse();
    }

    [Fact]
    public void FirmwareUpgradeOnTheSameBoot_KeepsItsOwnReason()
    {
        var upgrade = new DeviceRebootReason(RebootCategory.FirmwareUpgrade, "Firmware upgrade", "6.0.7", RebootReasonSource.DeviceState);

        DeviceRebootTracker.ApplyPlannedRestart(upgrade, Restart()).Should().BeNull();
    }

    [Fact]
    public void DeviceBelow_OnlyAPowerLossBootIsOurs()
    {
        DeviceRebootTracker.ApplyPlannedRestart(Clean, Restart(lostPowerOnly: true)).Should().BeNull();
        DeviceRebootTracker.ApplyPlannedRestart(PowerLoss, Restart(lostPowerOnly: true))!.IsUnexpected.Should().BeFalse();
    }
}

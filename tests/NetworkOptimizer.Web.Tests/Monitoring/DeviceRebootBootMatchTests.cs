using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Which uptime samples still describe the boot on record. The tolerance absorbs sampling jitter,
/// but a device reflashed twice in a row restarts well inside it, and the second change has to be
/// recorded rather than folded into the first.
/// </summary>
public class DeviceRebootBootMatchTests
{
    private static readonly DateTime Boot = new(2026, 8, 14, 15, 20, 43, DateTimeKind.Utc);

    [Fact]
    public void JitteredSampleOfTheSameBootIsTheSameBoot()
    {
        DeviceRebootTracker.IsSameBoot(Boot, "8.7.11.19419", Boot.AddSeconds(4), "8.7.11.19419")
            .Should().BeTrue();
    }

    [Fact]
    public void RestartOutsideToleranceIsANewBoot()
    {
        DeviceRebootTracker.IsSameBoot(Boot, "8.7.11.19419", Boot.AddHours(3), "8.7.11.19419")
            .Should().BeFalse();
    }

    /// <summary>
    /// The reflash pair that went unrecorded: a downgrade at 15:20:43 and the roll-forward behind
    /// it at 15:24:04. Three minutes apart is inside the tolerance, so only the firmware separates
    /// them - and a device cannot swap images without restarting.
    /// </summary>
    [Fact]
    public void SecondReflashInsideToleranceIsANewBoot()
    {
        DeviceRebootTracker.IsSameBoot(
            Boot, "8.7.9.19401", Boot.AddMinutes(3).AddSeconds(21), "8.7.11.19419")
            .Should().BeFalse();
    }

    [Fact]
    public void SameImageInTheConsolesTwoShapesIsStillTheSameBoot()
    {
        DeviceRebootTracker.IsSameBoot(Boot, "8.7.11", Boot.AddSeconds(6), "8.7.11.19419")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "8.7.11.19419")]
    [InlineData("8.7.11.19419", null)]
    [InlineData(null, null)]
    public void UnknownFirmwareLeavesTheBootMatchToTheInstantAlone(string? recorded, string? sampled)
    {
        DeviceRebootTracker.IsSameBoot(Boot, recorded, Boot.AddSeconds(4), sampled)
            .Should().BeTrue();
    }
}

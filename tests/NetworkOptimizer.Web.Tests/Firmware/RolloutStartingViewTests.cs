using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Components.Pages;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>When the Firmware Rollout page shows a waiting plan as starting (the map, not Clear to Deploy?).</summary>
public class RolloutStartingViewTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 19, 15, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(FirmwareRolloutStatus.Scheduled)]
    [InlineData(FirmwareRolloutStatus.Announced)]
    public void WaitingPlan_WithAFreshMark_IsStarting(FirmwareRolloutStatus status) =>
        FirmwareRollout.IsStarting(status, deployingNow: false, Now.AddMinutes(-1), Now).Should().BeTrue();

    [Fact]
    public void WaitingPlan_WhileThisPageDeploysIt_IsStartingBeforeAnyMark() =>
        FirmwareRollout.IsStarting(FirmwareRolloutStatus.Announced, deployingNow: true, null, Now).Should().BeTrue();

    [Fact]
    public void WaitingPlan_WithoutAMark_IsNotStarting() =>
        FirmwareRollout.IsStarting(FirmwareRolloutStatus.Announced, deployingNow: false, null, Now).Should().BeFalse();

    [Fact]
    public void StaleMark_FromAStartTheProcessDiedInside_ReadsAsWaiting() =>
        FirmwareRollout.IsStarting(FirmwareRolloutStatus.Announced, deployingNow: false,
            Now - FirmwareRollout.StartingShownFor, Now).Should().BeFalse();

    [Theory]
    [InlineData(FirmwareRolloutStatus.Running)]
    [InlineData(FirmwareRolloutStatus.Paused)]
    [InlineData(FirmwareRolloutStatus.SoakWait)]
    [InlineData(FirmwareRolloutStatus.Reported)]
    [InlineData(FirmwareRolloutStatus.Aborted)]
    [InlineData(FirmwareRolloutStatus.Failed)]
    [InlineData(FirmwareRolloutStatus.Draft)]
    public void AnyOtherStatus_IsNeverStarting(FirmwareRolloutStatus status) =>
        FirmwareRollout.IsStarting(status, deployingNow: true, Now, Now).Should().BeFalse();
}

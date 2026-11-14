using FluentAssertions;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class FanSpeedGuardTests
{
    private const string Device = "aa:bb:cc:dd:ee:ff";

    [Fact]
    public void AZeroAfterASpinningFan_IsHeldBack_UntilASecondAgrees()
    {
        var guard = new FanSpeedGuard();

        guard.Accept(Device, 1900).Should().Be(1900);
        guard.Accept(Device, 0).Should().BeNull("one zero is an SNMP agent restarting");
        guard.Accept(Device, 0).Should().Be(0, "two in a row is a fan that stopped");
        guard.Accept(Device, 0).Should().Be(0);
    }

    [Fact]
    public void ASingleZero_BetweenNormalReadings_IsDropped()
    {
        var guard = new FanSpeedGuard();

        guard.Accept(Device, 1900).Should().Be(1900);
        guard.Accept(Device, 0).Should().BeNull();
        guard.Accept(Device, 1912).Should().Be(1912);
        guard.Accept(Device, 0).Should().BeNull("the next lone zero is held back as well");
    }

    [Fact]
    public void AFanNeverSeenSpinning_ReportsZero()
    {
        var guard = new FanSpeedGuard();

        guard.Accept(Device, 0).Should().Be(0);
        guard.Accept(Device, null).Should().BeNull();
    }

    [Fact]
    public void DevicesAreTrackedApart()
    {
        var guard = new FanSpeedGuard();

        guard.Accept(Device, 1900);
        guard.Accept("00:11:22:33:44:55", 0).Should().Be(0);
    }
}

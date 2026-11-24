using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>The restart and power-cycle bodies match what UniFi Network's own device actions send.</summary>
public class UniFiDeviceRestartCommandTests
{
    [Fact]
    public void RestartBody_IsASoftRestartWithANormalizedMac()
    {
        var body = UniFiDeviceRestartCommand.BuildRestartBody("AA-BB-CC-DD-EE-FF");

        body["cmd"].Should().Be("restart");
        body["mac"].Should().Be("aa:bb:cc:dd:ee:ff");
        body["reboot_type"].Should().Be("soft");
    }

    [Fact]
    public void PowerCycleBody_NamesTheSwitchAndPort()
    {
        var body = UniFiDeviceRestartCommand.BuildPowerCycleBody("AABBCCDDEEFF", 7);

        body["cmd"].Should().Be("power-cycle");
        body["mac"].Should().Be("aa:bb:cc:dd:ee:ff");
        body["port_idx"].Should().Be(7);
    }

    [Fact]
    public void PowerCycleBody_PortBelowOne_Throws()
    {
        var act = () => UniFiDeviceRestartCommand.BuildPowerCycleBody("aa:bb:cc:dd:ee:ff", 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

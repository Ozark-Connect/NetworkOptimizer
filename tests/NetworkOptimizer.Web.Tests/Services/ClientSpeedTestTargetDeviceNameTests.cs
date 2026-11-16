using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class ClientSpeedTestTargetDeviceNameTests
{
    private static NetworkPath PathStartingAt(HopType type, string? name) => new()
    {
        Hops =
        [
            new NetworkHop { Order = 0, Type = type, DeviceName = name ?? "" },
            new NetworkHop { Order = 1, Type = HopType.Switch, DeviceName = "Switch1" },
            new NetworkHop { Order = 2, Type = HopType.Server, DeviceName = "Server" }
        ]
    };

    [Theory]
    [InlineData(HopType.AccessPoint, "AP1")]
    [InlineData(HopType.Switch, "Switch2")]
    [InlineData(HopType.Gateway, "Gateway")]
    public void UniFiDeviceAtPathStart_ReturnsItsName(HopType type, string name)
    {
        ClientSpeedTestService.TargetDeviceName(PathStartingAt(type, name)).Should().Be(name);
    }

    [Theory]
    [InlineData(HopType.Client)]
    [InlineData(HopType.WirelessClient)]
    [InlineData(HopType.Wan)]
    public void NonDeviceAtPathStart_ReturnsNull(HopType type)
    {
        ClientSpeedTestService.TargetDeviceName(PathStartingAt(type, "Laptop")).Should().BeNull();
    }

    [Fact]
    public void DeviceWithoutName_ReturnsNull()
    {
        ClientSpeedTestService.TargetDeviceName(PathStartingAt(HopType.AccessPoint, null)).Should().BeNull();
    }

    [Fact]
    public void EmptyPath_ReturnsNull()
    {
        ClientSpeedTestService.TargetDeviceName(new NetworkPath()).Should().BeNull();
    }
}

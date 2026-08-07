using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class MonitoringCollectionAgentTests
{
    [Theory]
    [InlineData(DeviceType.Gateway, "gateway")]
    [InlineData(DeviceType.Switch, "switch")]
    [InlineData(DeviceType.AccessPoint, "ap")]
    [InlineData(DeviceType.SmartPower, "smartpower")]
    [InlineData(DeviceType.Unknown, "unknown")]
    public void DescribeDeviceType_ReturnsStableMonitoringLabel(DeviceType type, string expected)
    {
        MonitoringCollectionAgent.DescribeDeviceType(type).Should().Be(expected);
    }
}
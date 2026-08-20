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
    [InlineData(DeviceType.CellularModem, "unknown")]
    public void DescribeDeviceType_ReturnsStableMonitoringLabel(DeviceType type, string expected)
    {
        MonitoringCollectionAgent.DescribeDeviceType(type).Should().Be(expected);
    }

    /// <summary>
    /// The label is a series key, so the enum's own name is not interchangeable with it. Writing
    /// ToString() alongside the canonical label is what forked an AP into "ap" and "accesspoint",
    /// leaving one device showing up twice in anything that groups by device.
    /// </summary>
    [Theory]
    [InlineData(DeviceType.AccessPoint)]
    [InlineData(DeviceType.CellularModem)]
    public void DescribeDeviceType_DoesNotMatchTheEnumName(DeviceType type)
    {
        MonitoringCollectionAgent.DescribeDeviceType(type)
            .Should().NotBe(type.ToString().ToLowerInvariant());
    }
}
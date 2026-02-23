using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

using DeviceType = NetworkOptimizer.Monitoring.Models.DeviceType;

namespace NetworkOptimizer.Monitoring.Tests;

public class AlertThresholdTests
{
    #region IsExceeded Tests

    [Theory]
    [InlineData(ThresholdComparison.GreaterThan, 90, 95, true)]
    [InlineData(ThresholdComparison.GreaterThan, 90, 90, false)]
    [InlineData(ThresholdComparison.GreaterThan, 90, 85, false)]
    public void IsExceeded_GreaterThan_ReturnsCorrectResult(ThresholdComparison comparison, double threshold, double value, bool expected)
    {
        var alertThreshold = new AlertThreshold { Value = threshold, Comparison = comparison };
        alertThreshold.IsExceeded(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(ThresholdComparison.GreaterThanOrEqual, 90, 95, true)]
    [InlineData(ThresholdComparison.GreaterThanOrEqual, 90, 90, true)]
    [InlineData(ThresholdComparison.GreaterThanOrEqual, 90, 85, false)]
    public void IsExceeded_GreaterThanOrEqual_ReturnsCorrectResult(ThresholdComparison comparison, double threshold, double value, bool expected)
    {
        var alertThreshold = new AlertThreshold { Value = threshold, Comparison = comparison };
        alertThreshold.IsExceeded(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(ThresholdComparison.LessThan, 10, 5, true)]
    [InlineData(ThresholdComparison.LessThan, 10, 10, false)]
    [InlineData(ThresholdComparison.LessThan, 10, 15, false)]
    public void IsExceeded_LessThan_ReturnsCorrectResult(ThresholdComparison comparison, double threshold, double value, bool expected)
    {
        var alertThreshold = new AlertThreshold { Value = threshold, Comparison = comparison };
        alertThreshold.IsExceeded(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(ThresholdComparison.LessThanOrEqual, 10, 5, true)]
    [InlineData(ThresholdComparison.LessThanOrEqual, 10, 10, true)]
    [InlineData(ThresholdComparison.LessThanOrEqual, 10, 15, false)]
    public void IsExceeded_LessThanOrEqual_ReturnsCorrectResult(ThresholdComparison comparison, double threshold, double value, bool expected)
    {
        var alertThreshold = new AlertThreshold { Value = threshold, Comparison = comparison };
        alertThreshold.IsExceeded(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(ThresholdComparison.Equal, 50, 50, true)]
    [InlineData(ThresholdComparison.Equal, 50, 50.0001, true)] // Within tolerance
    [InlineData(ThresholdComparison.Equal, 50, 51, false)]
    public void IsExceeded_Equal_ReturnsCorrectResult(ThresholdComparison comparison, double threshold, double value, bool expected)
    {
        var alertThreshold = new AlertThreshold { Value = threshold, Comparison = comparison };
        alertThreshold.IsExceeded(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(ThresholdComparison.NotEqual, 50, 51, true)]
    [InlineData(ThresholdComparison.NotEqual, 50, 50, false)]
    public void IsExceeded_NotEqual_ReturnsCorrectResult(ThresholdComparison comparison, double threshold, double value, bool expected)
    {
        var alertThreshold = new AlertThreshold { Value = threshold, Comparison = comparison };
        alertThreshold.IsExceeded(value).Should().Be(expected);
    }

    #endregion

    #region IsActiveNow Tests

    [Fact]
    public void IsActiveNow_DisabledThreshold_ReturnsFalse()
    {
        var threshold = new AlertThreshold { IsEnabled = false };
        threshold.IsActiveNow().Should().BeFalse();
    }

    [Fact]
    public void IsActiveNow_NoTimeWindows_ReturnsTrue()
    {
        var threshold = new AlertThreshold { IsEnabled = true };
        threshold.IsActiveNow().Should().BeTrue();
    }

    #endregion

    #region AppliesTo Device Tests

    [Fact]
    public void AppliesTo_NoTargets_ReturnsTrue()
    {
        var threshold = new AlertThreshold();
        var device = CreateDeviceMetrics("192.168.1.1", "Switch-1", DeviceType.Switch);

        threshold.AppliesTo(device).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_MatchingDeviceIp_ReturnsTrue()
    {
        var threshold = new AlertThreshold
        {
            TargetDevices = new List<string> { "192.168.1.1", "192.168.1.2" }
        };
        var device = CreateDeviceMetrics("192.168.1.1", "Switch-1", DeviceType.Switch);

        threshold.AppliesTo(device).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_NonMatchingDeviceIp_ReturnsFalse()
    {
        var threshold = new AlertThreshold
        {
            TargetDevices = new List<string> { "192.168.1.1", "192.168.1.2" }
        };
        var device = CreateDeviceMetrics("192.168.1.100", "Switch-1", DeviceType.Switch);

        threshold.AppliesTo(device).Should().BeFalse();
    }

    [Fact]
    public void AppliesTo_MatchingDeviceType_ReturnsTrue()
    {
        var threshold = new AlertThreshold
        {
            TargetDeviceTypes = new List<DeviceType> { DeviceType.Switch, DeviceType.Gateway }
        };
        var device = CreateDeviceMetrics("192.168.1.1", "Switch-1", DeviceType.Switch);

        threshold.AppliesTo(device).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_NonMatchingDeviceType_ReturnsFalse()
    {
        var threshold = new AlertThreshold
        {
            TargetDeviceTypes = new List<DeviceType> { DeviceType.Gateway }
        };
        var device = CreateDeviceMetrics("192.168.1.1", "Switch-1", DeviceType.Switch);

        threshold.AppliesTo(device).Should().BeFalse();
    }

    #endregion

    #region AppliesTo Interface Tests

    [Fact]
    public void AppliesTo_Interface_NoTargets_ReturnsTrue()
    {
        var threshold = new AlertThreshold();
        var iface = CreateInterfaceMetrics("eth0", "WAN Port");

        threshold.AppliesTo(iface).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_Interface_MatchingDescription_ReturnsTrue()
    {
        var threshold = new AlertThreshold
        {
            TargetInterfaces = new List<string> { "WAN" }
        };
        var iface = CreateInterfaceMetrics("eth0", "WAN Port");

        threshold.AppliesTo(iface).Should().BeTrue();
    }

    [Fact]
    public void AppliesTo_Interface_NonMatchingDescription_ReturnsFalse()
    {
        var threshold = new AlertThreshold
        {
            TargetInterfaces = new List<string> { "LAN" }
        };
        var iface = CreateInterfaceMetrics("eth0", "WAN Port");

        threshold.AppliesTo(iface).Should().BeFalse();
    }

    #endregion

    #region Helper Methods

    private static DeviceMetrics CreateDeviceMetrics(string ip, string hostname, DeviceType deviceType)
    {
        return new DeviceMetrics
        {
            IpAddress = ip,
            Hostname = hostname,
            DeviceType = deviceType,
            CpuUsage = 50,
            MemoryUsage = 60
        };
    }

    private static InterfaceMetrics CreateInterfaceMetrics(string name, string description)
    {
        return new InterfaceMetrics
        {
            Name = name,
            Description = description,
            DeviceIp = "192.168.1.1",
            DeviceHostname = "Switch-1",
            Index = 1
        };
    }

    #endregion
}

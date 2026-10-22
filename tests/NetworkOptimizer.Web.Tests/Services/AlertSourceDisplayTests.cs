using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class AlertSourceDisplayTests
{
    [Theory]
    [InlineData("rollout", "Firmware Rollout")]
    [InlineData("console", "UniFi Console")]
    [InlineData("cable_modem", "Cable Modem")]
    [InlineData("speedtest", "Speed Test")]
    [InlineData("wifi", "Wi-Fi")]
    public void KnownSource_ShowsItsDisplayName(string source, string expected)
    {
        AlertSourceDisplay.GetName(source).Should().Be(expected);
    }

    [Fact]
    public void UnknownSource_ShowsAsStored()
    {
        AlertSourceDisplay.GetName("newsource").Should().Be("newsource");
    }

    [Fact]
    public void NullSource_ShowsNothing()
    {
        AlertSourceDisplay.GetName(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("agent", "Agent")]
    [InlineData("agent", " agent ")]
    [InlineData("device", "")]
    [InlineData("device", null)]
    public void DeviceName_RepeatingSourceOrEmpty_IsDropped(string source, string? deviceName)
    {
        AlertSourceDisplay.GetDeviceName(source, deviceName).Should().BeNull();
    }

    [Theory]
    [InlineData("agent", "Agent 2")]
    [InlineData("device", "Office Switch")]
    public void DeviceName_Distinct_IsKept(string source, string deviceName)
    {
        AlertSourceDisplay.GetDeviceName(source, deviceName).Should().Be(deviceName);
    }
}

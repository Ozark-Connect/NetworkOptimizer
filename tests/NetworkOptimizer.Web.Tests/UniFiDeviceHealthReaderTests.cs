using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class UniFiDeviceHealthReaderTests
{
    private static UniFiDeviceResponse WithTemperatures(string json) =>
        new() { Temperatures = JsonDocument.Parse(json).RootElement };

    [Fact]
    public void ParseDeviceTemperature_ScalesGatewayMillidegrees()
    {
        var device = WithTemperatures("""[{"name":"CPU","type":"cpu","value":48000}]""");

        UniFiDeviceHealthReader.ParseDeviceTemperature(device).Should().BeApproximately(48, 0.001);
    }

    [Fact]
    public void ParseDeviceTemperature_LeavesCelsiusAlone()
    {
        var device = WithTemperatures("""[{"name":"CPU","type":"cpu","value":62.5}]""");

        UniFiDeviceHealthReader.ParseDeviceTemperature(device).Should().BeApproximately(62.5, 0.001);
    }

    [Fact]
    public void ParseDeviceTemperature_ScalesSwitchGeneralTemperature()
    {
        var device = new UniFiDeviceResponse { GeneralTemperature = 72000 };

        UniFiDeviceHealthReader.ParseDeviceTemperature(device).Should().BeApproximately(72, 0.001);
    }
}

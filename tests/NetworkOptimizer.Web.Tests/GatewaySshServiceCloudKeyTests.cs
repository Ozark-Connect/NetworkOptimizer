using FluentAssertions;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class GatewaySshServiceCloudKeyTests
{
    [Theory]
    [InlineData("Connection_OK\nUCKP.apq8053.v6.0.10.8e20374.260922.0941\n\nUbiquiti Networks, Inc. APQ8053 CloudKey Plus")]
    [InlineData("Connection_OK\nUCKG2.apq8053.v4.1.13.0000000.230101.0000\n\n")]
    [InlineData("Connection_OK\n\nUbiquiti Networks, Inc. APQ8053 CloudKey Plus")]
    public void IsCloudKey_CloudKeyOutput_True(string output)
        => GatewaySshService.IsCloudKey(output).Should().BeTrue();

    [Theory]
    [InlineData("Connection_OK\nUXGA6AA.ipq9574.v6.0.5.0b3cb18.260825.1528\n\nQualcomm Technologies, Inc. IPQ9574/AP-AL02-C19")]
    [InlineData("Connection_OK\nUDMPRO.al324.v4.3.6.0000000.250101.0000\n\n")]
    [InlineData("Connection_OK\nUCGF.ipq5322.v6.0.10.0000000.260901.0000\n\n")]
    [InlineData("Connection_OK\n\n")]
    public void IsCloudKey_GatewayOrUnknownOutput_False(string output)
        => GatewaySshService.IsCloudKey(output).Should().BeFalse();
}

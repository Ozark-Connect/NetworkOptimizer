using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class SelfIpDetectionTests
{
    private static Func<string, string?> Env(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void NotInContainer_UsesDetectedAddress()
    {
        var ip = ThreatCollectionService.DetectSelfIpv4(
            Env(new()), () => ["192.0.2.10"], () => "192.0.2.10");

        ip.Should().Be("192.0.2.10");
    }

    [Fact]
    public void BridgeContainer_WithoutHostIp_ReturnsNull()
    {
        var ip = ThreatCollectionService.DetectSelfIpv4(
            Env(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }),
            () => ["172.18.0.5"],
            () => "172.18.0.5");

        ip.Should().BeNull();
    }

    [Fact]
    public void BridgeContainer_WithHostIp_UsesHostIp()
    {
        var ip = ThreatCollectionService.DetectSelfIpv4(
            Env(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true", ["HOST_IP"] = "192.0.2.20" }),
            () => ["172.18.0.5"],
            () => "192.0.2.20");

        ip.Should().Be("192.0.2.20");
    }

    [Fact]
    public void HostNetworkContainer_SeesHostInterfaces_UsesDetectedAddress()
    {
        var ip = ThreatCollectionService.DetectSelfIpv4(
            Env(new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" }),
            () => ["192.0.2.30", "172.17.0.1"],
            () => "192.0.2.30");

        ip.Should().Be("192.0.2.30");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("2001:db8::1")]
    public void UnusableDetection_ReturnsNull(string? detected)
    {
        var ip = ThreatCollectionService.DetectSelfIpv4(
            Env(new()), () => ["192.0.2.10"], () => detected);

        ip.Should().BeNull();
    }
}

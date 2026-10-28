using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class TunnelReconnectPolicyTests
{
    [Fact]
    public void NeverOpened_KeepsTheBackoff()
    {
        // A failed dial, a missing hello, or a refusal (revoked, license-restricted).
        TunnelReconnectPolicy.RedialsAtOnce(null).Should().BeFalse();
        TunnelReconnectPolicy.Delay(null, new Random(1)).Should().Be(TunnelReconnectPolicy.Backoff);
    }

    [Fact]
    public void ShortRun_KeepsTheBackoff()
    {
        // A proxy that cuts the tunnel every minute or two must not be redialed at once.
        var openFor = TunnelReconnectPolicy.HealthyRun - TimeSpan.FromSeconds(1);

        TunnelReconnectPolicy.RedialsAtOnce(openFor).Should().BeFalse();
        TunnelReconnectPolicy.Delay(openFor, new Random(1)).Should().Be(TunnelReconnectPolicy.Backoff);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(42)]
    [InlineData(600)]
    public void HealthyRun_RedialsWithinOneToThreeSeconds(int minutes)
    {
        var openFor = TimeSpan.FromMinutes(minutes);
        var random = new Random(7);

        TunnelReconnectPolicy.RedialsAtOnce(openFor).Should().BeTrue();
        for (var i = 0; i < 200; i++)
            TunnelReconnectPolicy.Delay(openFor, random).Should()
                .BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1))
                .And.BeLessThan(TimeSpan.FromSeconds(3));
    }
}

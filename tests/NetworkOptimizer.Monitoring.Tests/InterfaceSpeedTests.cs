using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// How a port's speed is chosen between the slow-tier metadata walk and the medium-tier
/// ifHighSpeed walk. A port renegotiates without a label change, so the medium reading wins
/// whenever it says something.
/// </summary>
public class InterfaceSpeedTests
{
    [Fact]
    public void RenegotiatedSpeedReplacesTheMetadata()
    {
        // A WOL port idling at 100 Mbps, then up at 2.5 Gbps once its host boots.
        var (speed, highSpeed) = SnmpPoller.ResolveSpeed(100_000_000, 100, 2500);
        Assert.Equal(2500, highSpeed);
        Assert.Equal(2_500_000_000, speed);
    }

    [Fact]
    public void DerivedIfSpeedSaturatesLikeTheGauge()
    {
        var (speed, highSpeed) = SnmpPoller.ResolveSpeed(1_000_000_000, 1000, 10000);
        Assert.Equal(10000, highSpeed);
        Assert.Equal(uint.MaxValue, speed);
    }

    [Fact]
    public void MatchingReadingKeepsTheMetadataIfSpeed()
    {
        Assert.Equal((1_000_000_000L, 1000L), SnmpPoller.ResolveSpeed(1_000_000_000, 1000, 1000));
    }

    [Fact]
    public void ZeroReadingKeepsTheMetadata()
    {
        // A device that carries its speed in ifSpeed alone reports ifHighSpeed 0.
        Assert.Equal((100_000_000L, 0L), SnmpPoller.ResolveSpeed(100_000_000, 0, 0));
    }

    [Fact]
    public void MissingReadingKeepsTheMetadata()
    {
        Assert.Equal((2_500_000_000L, 2500L), SnmpPoller.ResolveSpeed(2_500_000_000, 2500, 0));
    }
}

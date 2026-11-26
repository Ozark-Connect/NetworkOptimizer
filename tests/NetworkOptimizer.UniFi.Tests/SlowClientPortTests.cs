using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>A 10/100 note is checked against history only when the slow link is a client's own switch port.</summary>
public class SlowClientPortTests
{
    private const string Client = "aa:bb:cc:00:00:02";
    private const string Switch = "aa:bb:cc:00:00:01";

    private static PathAnalysisResult Result(int clientMbps, int uplinkMbps, bool withNote = true)
    {
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TheoreticalMaxMbps = Math.Min(clientMbps, uplinkMbps),
                Hops =
                [
                    new NetworkHop { Order = 0, Type = HopType.Client, DeviceMac = Client, EgressPort = 6, EgressSpeedMbps = clientMbps, IngressSpeedMbps = clientMbps },
                    new NetworkHop { Order = 1, Type = HopType.Switch, DeviceMac = Switch, EgressSpeedMbps = uplinkMbps, IngressSpeedMbps = clientMbps },
                    new NetworkHop { Order = 2, Type = HopType.Gateway, DeviceMac = "aa:bb:cc:00:00:03" },
                ],
            },
        };
        if (withNote) result.Recommendations.Add(PathAnalysisResult.SlowLinkRecommendation);
        return result;
    }

    [Fact]
    public void AClientsOwn100MbpsPortIsFound()
    {
        Result(clientMbps: 100, uplinkMbps: 1000).SlowClientPort().Should().Be((Client, Switch, 6, 100));
    }

    [Fact]
    public void A100MbpsLinkBetweenSwitchesIsNotAClientPort()
    {
        Result(clientMbps: 1000, uplinkMbps: 100).SlowClientPort().Should().BeNull();
    }

    [Fact]
    public void NoNoteNoPort()
    {
        Result(clientMbps: 100, uplinkMbps: 1000, withNote: false).SlowClientPort().Should().BeNull();
    }
}

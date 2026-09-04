using NetworkOptimizer.Web.Services.Tours;
using Xunit;
using static NetworkOptimizer.Web.Services.Tours.TourUrlTokenResolver;

namespace NetworkOptimizer.Web.Tests.Tours;

public class TourUrlTokenResolverTests
{
    [Fact]
    public void PicksTheClientWithTheMostLanTests()
    {
        var pick = PickWifiClient(new[]
        {
            new WifiClientCandidate("192.0.2.10", 2, NamedPhone: true, DetectedPhone: true),
            new WifiClientCandidate("192.0.2.11", 7, NamedPhone: false, DetectedPhone: false),
            new WifiClientCandidate("192.0.2.12", 0, NamedPhone: false, DetectedPhone: false),
        });
        Assert.Equal("192.0.2.11", pick);
    }

    [Fact]
    public void FallsBackToANamedPhoneThenADetectedPhone()
    {
        var named = PickWifiClient(new[]
        {
            new WifiClientCandidate("192.0.2.10", 0, NamedPhone: false, DetectedPhone: true),
            new WifiClientCandidate("192.0.2.11", 0, NamedPhone: true, DetectedPhone: false),
        });
        Assert.Equal("192.0.2.11", named);

        var detected = PickWifiClient(new[]
        {
            new WifiClientCandidate("192.0.2.10", 0, NamedPhone: false, DetectedPhone: false),
            new WifiClientCandidate("192.0.2.11", 0, NamedPhone: false, DetectedPhone: true),
        });
        Assert.Equal("192.0.2.11", detected);
    }

    [Fact]
    public void FallsBackToTheFirstClientAndNullWhenThereAreNone()
    {
        var first = PickWifiClient(new[]
        {
            new WifiClientCandidate("192.0.2.10", 0, NamedPhone: false, DetectedPhone: false),
            new WifiClientCandidate("192.0.2.11", 0, NamedPhone: false, DetectedPhone: false),
        });
        Assert.Equal("192.0.2.10", first);
        Assert.Null(PickWifiClient(Array.Empty<WifiClientCandidate>()));
    }
}

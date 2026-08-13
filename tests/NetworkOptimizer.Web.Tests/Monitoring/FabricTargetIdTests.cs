using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class FabricTargetIdTests
{
    private const string Mac = "aa:bb:cc:dd:ee:ff";

    private static Dictionary<string, MonitoringTarget> Taken(params string[] ids) =>
        ids.ToDictionary(id => id, id => new MonitoringTarget { TargetId = id });

    [Fact]
    public void FirstTargetForADeviceKeepsTheBareId()
    {
        Assert.Equal($"fabric-{Mac}", MonitoringCollectionAgent.NextFabricTargetId(Mac, "192.0.2.10", Taken()));
    }

    [Fact]
    public void SecondAddressIsQualifiedByThatAddress()
    {
        var id = MonitoringCollectionAgent.NextFabricTargetId(Mac, "192.0.2.20", Taken($"fabric-{Mac}"));
        Assert.Equal($"fabric-{Mac}-192-0-2-20", id);
    }

    [Fact]
    public void AnAddressAlreadySpokenForGetsACounter()
    {
        var id = MonitoringCollectionAgent.NextFabricTargetId(
            Mac, "192.0.2.20", Taken($"fabric-{Mac}", $"fabric-{Mac}-192-0-2-20"));
        Assert.Equal($"fabric-{Mac}-192-0-2-20-2", id);
    }

    [Fact]
    public void IdsStayUniqueAcrossRepeatedCollisions()
    {
        var taken = Taken($"fabric-{Mac}", $"fabric-{Mac}-192-0-2-20", $"fabric-{Mac}-192-0-2-20-2");
        var id = MonitoringCollectionAgent.NextFabricTargetId(Mac, "192.0.2.20", taken);
        Assert.Equal($"fabric-{Mac}-192-0-2-20-3", id);
    }

    [Fact]
    public void IPv6AddressesProduceAnIdWithNoColons()
    {
        var id = MonitoringCollectionAgent.NextFabricTargetId(Mac, "2001:db8::1", Taken($"fabric-{Mac}"));
        Assert.DoesNotContain(':', id[$"fabric-".Length..].Replace(Mac, ""));
        Assert.Equal($"fabric-{Mac}-2001-db8--1", id);
    }
}

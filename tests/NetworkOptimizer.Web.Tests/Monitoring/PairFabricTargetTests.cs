using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// After a Shadow Mode failover the VIP's new holder takes over the other member's fabric target,
/// so the gateway keeps one target and one latency history.
/// </summary>
public class PairFabricTargetTests
{
    private const string A = "aa:bb:cc:00:00:01";
    private const string B = "aa:bb:cc:00:00:02";
    private const string Vip = "192.0.2.1";

    private static MonitoringTarget Target(string mac, string address, bool retired = false, bool auto = true) => new()
    {
        TargetId = "fabric-" + mac,
        Name = "Gateway",
        Address = address,
        DeviceMac = mac,
        TargetType = MonitoringTargetType.Fabric,
        AutoDiscovered = auto,
        RetiredAt = retired ? DateTime.UtcNow : null,
    };

    [Fact]
    public void PairMember_TakesThePartnersTargetAtTheVip()
    {
        var old = Target(A, Vip);

        MonitoringCollectionAgent.PairTargetFor([old], B, Vip, [A, B]).Should().BeSameAs(old);
    }

    [Fact]
    public void LiveBeforeRetired()
    {
        var retired = Target(A, Vip, retired: true);
        var live = Target("aa:bb:cc:00:00:03", Vip);

        MonitoringCollectionAgent.PairTargetFor([retired, live], B, Vip, [A, B, "aa:bb:cc:00:00:03"]).Should().BeSameAs(live);
    }

    [Theory]
    [InlineData(false, Vip, true)]     // not a pair member
    [InlineData(true, "192.0.2.9", true)] // different address
    [InlineData(true, Vip, false)]     // a target the user added
    public void NoTakeover(bool pairMember, string address, bool auto)
    {
        var target = Target(A, address, auto: auto);
        string[] pair = pairMember ? [A, B] : [];

        MonitoringCollectionAgent.PairTargetFor([target], B, Vip, pair).Should().BeNull();
    }
}

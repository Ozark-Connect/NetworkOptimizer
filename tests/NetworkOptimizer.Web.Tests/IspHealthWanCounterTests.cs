using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class IspHealthWanCounterTests
{
    [Fact]
    public void OneCounterPerWan_LiveCounterWinsOverARememberedSubInterface()
    {
        // A VLAN WAN: live is the physical port, the old profile row holds the sub-interface.
        var live = new[] { ("WAN", (string?)"eth4"), ("WAN2", (string?)"eth0") };
        var remembered = new[] { ("WAN", (string?)"eth4.100"), ("WAN2", (string?)"eth0") };

        IspHealthService.OneCounterPerWan(live, remembered)
            .Should().BeEquivalentTo(new[] { "eth4", "eth0" });
    }

    [Fact]
    public void OneCounterPerWan_RememberedStandsInForAWanTheConsoleOmits()
    {
        var live = new[] { ("WAN", (string?)"eth4") };
        var remembered = new[] { ("WAN", (string?)"eth4"), ("WAN3", (string?)"gre1") };

        IspHealthService.OneCounterPerWan(live, remembered)
            .Should().BeEquivalentTo(new[] { "eth4", "gre1" });
    }

    [Fact]
    public void OneCounterPerWan_GroupsMatchAcrossKeyAndGroupSpellings()
    {
        var live = new[] { ("wan2", (string?)"eth0") };
        var remembered = new[] { ("WAN2", (string?)"eth0.10") };

        IspHealthService.OneCounterPerWan(live, remembered)
            .Should().Equal("eth0");
    }

    [Fact]
    public void OneCounterPerWan_ALiveWanWithNoCounterFallsBackToItsProfile()
    {
        var live = new[] { ("WAN", (string?)null) };
        var remembered = new[] { ("WAN", (string?)"ppp0") };

        IspHealthService.OneCounterPerWan(live, remembered)
            .Should().Equal("ppp0");
    }

    [Fact]
    public void OneCounterPerWan_NothingKnownIsEmpty()
    {
        IspHealthService.OneCounterPerWan([], [])
            .Should().BeEmpty();
    }
}

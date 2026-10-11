using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class GatewayBlocklistOrderTests
{
    private static UniFiFirewallPolicy P(string id, long index, bool predefined = false, string src = "ext", string dst = "int") => new()
    {
        Id = id,
        Index = index,
        Predefined = predefined,
        Source = new UniFiFirewallPolicyEndpoint { ZoneId = src },
        Destination = new UniFiFirewallPolicyEndpoint { ZoneId = dst },
    };

    [Fact]
    public void OursMoveToTheFront_OthersKeepTheirOrder()
    {
        var policies = new[]
        {
            P("user-allow", 10000),
            P("user-allow-2", 10001),
            P("ours", 10002),
            P("predefined", 30001, predefined: true),
            P("port-forward", 30500),                 // UniFi places these
            P("late", 40000),
            P("other-pair", 10003, dst: "gw"),
        };

        var (before, after) = GatewayBlocklist.OrderWithOursFirst(policies, "ext", "int", new HashSet<string> { "ours" });

        before.Should().Equal("ours", "user-allow", "user-allow-2");
        after.Should().Equal("late");
    }

    [Fact]
    public void AlreadyFirst_StaysFirst()
    {
        var (before, _) = GatewayBlocklist.OrderWithOursFirst(
            [P("ours", 10000), P("user", 10001)], "ext", "int", new HashSet<string> { "ours" });

        before.Should().Equal("ours", "user");
    }
}

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

    [Theory]
    [InlineData("CUSTOM", new[] { "NEW" }, true)]
    [InlineData("ALL", new string[0], false)]
    [InlineData("CUSTOM", new[] { "NEW", "INVALID" }, false)]
    [InlineData("CUSTOM", new[] { "INVALID" }, false)]
    public void IsNewConnectionsOnly_OnlyExactlyNew(string type, string[] states, bool expected)
    {
        var policy = new UniFiFirewallPolicy { ConnectionStateType = type, ConnectionStates = [.. states] };
        GatewayBlocklist.IsNewConnectionsOnly(policy).Should().Be(expected);
    }

    [Fact]
    public void SourcePoliciesToNarrow_BlocklistAndPublicListBlocks_NeverExceptionAllows()
    {
        var state = new GatewayBlocklistState { GroupV4Id = "own4", GroupV6Id = "own6", FeedsV4Id = "feed4", FeedsV6Id = "feed6", FeedsOutV4Id = "out4" };
        UniFiFirewallPolicy Policy(string id, string action, string? sourceGroup, string type, string[]? states = null, string? destinationGroup = null) => new()
        {
            Id = id,
            Action = action,
            ConnectionStateType = type,
            ConnectionStates = states?.ToList(),
            Source = new UniFiFirewallPolicyEndpoint { ZoneId = "ext", IpGroupId = sourceGroup },
            Destination = new UniFiFirewallPolicyEndpoint { ZoneId = "int", IpGroupId = destinationGroup },
        };
        var policies = new[]
        {
            Policy("own-all", "BLOCK", "own4", "ALL"),
            Policy("own-new", "BLOCK", "own6", "CUSTOM", ["NEW"]),
            Policy("feed-all-v4", "BLOCK", "feed4", "ALL"),
            Policy("feed-all-v6", "BLOCK", "feed6", "ALL"),
            Policy("feed-new", "BLOCK", "feed4", "CUSTOM", ["NEW"]),
            Policy("exception-in", "ALLOW", "feed4", "ALL"),
            Policy("exception-out-reply", "ALLOW", "feed4", "CUSTOM", ["ESTABLISHED", "RELATED"]),
            Policy("outbound", "BLOCK", null, "ALL", destinationGroup: "out4"),
            Policy("user", "BLOCK", "other", "ALL"),
        };

        GatewayBlocklist.SourcePoliciesToNarrow(policies, state).Select(p => p.Id)
            .Should().Equal("own-all", "feed-all-v4", "feed-all-v6");
    }

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

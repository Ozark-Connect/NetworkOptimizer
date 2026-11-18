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
    public void SourcePoliciesToNarrow_FindsGroupsByManagedNameWhenTheStoredIdsAreStale()
    {
        // Ids from before a Network app upgrade re-keyed the groups.
        var state = new GatewayBlocklistState { GroupV4Id = "old-own4", GroupV6Id = "old-own6", FeedsV4Id = "old-feed4", FeedsV6Id = "old-feed6" };
        var groups = new[]
        {
            new UniFiFirewallGroup { Id = "new-feed4", Name = GatewayBlocklist.FeedsNameV4, GroupType = "address-group" },
            new UniFiFirewallGroup { Id = "new-feed6", Name = GatewayBlocklist.FeedsNameV6, GroupType = "ipv6-address-group" },
            new UniFiFirewallGroup { Id = "new-own4", Name = GatewayBlocklist.GroupNameV4, GroupType = "address-group" },
            new UniFiFirewallGroup { Id = "wrong-type", Name = GatewayBlocklist.FeedsNameV4, GroupType = "port-group" },
            new UniFiFirewallGroup { Id = "user", Name = "My Blocklist", GroupType = "address-group" },
        };
        UniFiFirewallPolicy Policy(string id, string action, string group) => new()
        {
            Id = id,
            Action = action,
            ConnectionStateType = "ALL",
            Source = new UniFiFirewallPolicyEndpoint { ZoneId = "ext", IpGroupId = group },
            Destination = new UniFiFirewallPolicyEndpoint { ZoneId = "int" },
        };
        var policies = new[]
        {
            Policy("feed-block", "BLOCK", "new-feed4"),
            Policy("feed6-block", "BLOCK", "new-feed6"),
            Policy("own-block", "BLOCK", "new-own4"),
            Policy("exception", "ALLOW", "new-feed4"),
            Policy("user-block", "BLOCK", "user"),
            Policy("port-group-block", "BLOCK", "wrong-type"),
        };

        var current = GatewayBlocklist.ManagedSourceGroupIds(groups);

        current.Should().BeEquivalentTo(["new-feed4", "new-feed6", "new-own4"]);
        GatewayBlocklist.SourcePoliciesToNarrow(policies, state).Should().BeEmpty();
        GatewayBlocklist.SourcePoliciesToNarrow(policies, state, current).Select(p => p.Id)
            .Should().Equal("feed-block", "feed6-block", "own-block");
    }

    [Fact]
    public void ExistingBlock_SkipsAnExceptionAllowOnTheSameZonesAndGroup()
    {
        UniFiFirewallPolicy Policy(string id, string action, string src, string dst, string? sourceGroup, string? destinationGroup) => new()
        {
            Id = id,
            Action = action,
            Source = new UniFiFirewallPolicyEndpoint { ZoneId = src, IpGroupId = sourceGroup },
            Destination = new UniFiFirewallPolicyEndpoint { ZoneId = dst, IpGroupId = destinationGroup },
        };
        var policies = new[]
        {
            Policy("exception-out-reply", "ALLOW", "ext", "int", "feed4", null), // ordered ahead of the Block
            Policy("feed-block", "BLOCK", "ext", "int", "feed4", null),
            Policy("exception-out", "ALLOW", "int", "ext", null, "out4"),
            Policy("out-block", "BLOCK", "int", "ext", null, "out4"),
        };

        GatewayBlocklist.ExistingBlock(policies, "ext", "int", "feed4", groupIsSource: true)!.Id.Should().Be("feed-block");
        GatewayBlocklist.ExistingBlock(policies, "int", "ext", "out4", groupIsSource: false)!.Id.Should().Be("out-block");
        GatewayBlocklist.ExistingBlock(policies.Where(p => p.Action == "ALLOW"), "ext", "int", "feed4", groupIsSource: true).Should().BeNull();
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

using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>Policies naming a Network List UniFi Network lacks for their IP version (issue #1424).</summary>
public class GatewayBlocklistBrokenGroupTests
{
    private static UniFiFirewallGroup G(string id, string type) => new() { Id = id, GroupType = type };

    private static UniFiFirewallPolicy P(string id, string ipVersion, string? sourceGroup = null, string? destinationGroup = null,
        string name = "Other") => new()
    {
        Id = id, Name = name, IpVersion = ipVersion, Enabled = true, Action = "BLOCK",
        Source = new UniFiFirewallPolicyEndpoint { ZoneId = "ext", IpGroupId = sourceGroup },
        Destination = new UniFiFirewallPolicyEndpoint { ZoneId = "lan", IpGroupId = destinationGroup },
    };

    private static readonly IReadOnlyDictionary<string, UniFiFirewallGroup> Groups =
        GatewayBlocklist.GroupsById([G("v4", "address-group"), G("v6", "ipv6-address-group")]);

    [Theory]
    [InlineData("IPV4", "v4", false)]
    [InlineData("IPV6", "v6", false)]
    [InlineData("IPV4", "gone", true)]
    [InlineData("IPV4", "v6", true)]
    [InlineData("IPV6", "v4", true)]
    public void SourceGroup_MustExistInThePolicysFamily(string ipVersion, string group, bool broken)
    {
        GatewayBlocklist.HasBrokenGroup(P("p", ipVersion, sourceGroup: group), Groups).Should().Be(broken);
    }

    [Fact]
    public void DestinationGroup_IsCheckedToo()
    {
        GatewayBlocklist.HasBrokenGroup(P("p", "IPV4", destinationGroup: "gone"), Groups).Should().BeTrue();
    }

    [Fact]
    public void LeftoverId_OnASideThatDoesNotMatchByList_IsIgnored()
    {
        var policy = P("p", "IPV4", sourceGroup: "gone");
        policy.Source.MatchingTargetType = "SPECIFIC";

        GatewayBlocklist.HasBrokenGroup(policy, Groups).Should().BeFalse();
    }

    [Fact]
    public void NoGroup_IsNotBroken()
    {
        GatewayBlocklist.HasBrokenGroup(P("p", "IPV4", sourceGroup: ""), Groups).Should().BeFalse();
    }

    [Fact]
    public void OwnBroken_TakesRecordedAndReservedNames_NeverOthers()
    {
        var state = new GatewayBlocklistState { Policies = [new GatewayBlockPolicyRef("recorded", "lan", "IPV4")] };
        var policies = new[]
        {
            P("recorded", "IPV4", sourceGroup: "gone"),
            P("named", "IPV4", sourceGroup: "v6", name: GatewayBlocklist.FeedsPolicyName),
            P("users", "IPV4", sourceGroup: "gone"),
            P("healthy", "IPV4", sourceGroup: "v4", name: GatewayBlocklist.PolicyName),
        };

        GatewayBlocklist.OwnBrokenPolicies(policies, state, Groups).Select(p => p.Id).Should().BeEquivalentTo("recorded", "named");
    }

    [Fact]
    public void Verify_ReportsAPolicyOnAMissingGroup()
    {
        var state = new GatewayBlocklistState
        {
            FeedsV4Id = "v4", FeedsV6Id = "v6",
            Policies = [new GatewayBlockPolicyRef("ours", "lan", "IPV4")],
        };

        GatewayBlocklist.Verify(state, [P("ours", "IPV4", sourceGroup: "gone")], [G("v4", "address-group")])
            .Should().StartWith("1 of Network Optimizer's Block policies name a Network List");
        GatewayBlocklist.Verify(state, [P("ours", "IPV4", sourceGroup: "v4")], [G("v4", "address-group")]).Should().BeNull();
    }
}

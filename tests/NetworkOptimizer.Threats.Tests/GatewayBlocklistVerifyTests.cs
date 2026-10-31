using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class GatewayBlocklistVerifyTests
{
    private static UniFiFirewallPolicy P(string id, bool enabled = true, bool predefined = false) =>
        new() { Id = id, Enabled = enabled, Predefined = predefined };

    private static GatewayBlocklistState State(params string[] policyIds) => new()
    {
        FeedsV4Id = "feeds-v4",
        FeedsV6Id = "feeds-v6",
        Policies = policyIds.Select(id => new GatewayBlockPolicyRef(id, "zone", "IPV4")).ToList(),
    };

    [Fact]
    public void RepeatedConsoleIds_DoNotThrow()
    {
        // Seen on a UDM Pro: the console listed one policy id twice (issue #1333).
        const string repeated = "repeated-id";
        var policies = new[] { P(repeated, predefined: true), P(repeated, predefined: true), P("ours-1"), P("ours-2") };

        GatewayBlocklist.Verify(State("ours-1", "ours-2"), policies).Should().BeNull();
    }

    [Fact]
    public void MissingPolicy_IsReported()
    {
        GatewayBlocklist.Verify(State("ours-1", "ours-2"), [P("ours-1"), P("dup"), P("dup")])
            .Should().StartWith("1 of Network Optimizer's Block policies were removed");
    }

    [Fact]
    public void DisabledPolicy_IsReported()
    {
        GatewayBlocklist.Verify(State("ours-1"), [P("ours-1", enabled: false)])
            .Should().StartWith("1 of Network Optimizer's Block policies are disabled");
    }

    [Fact]
    public void NoPublicListGroups_AsksForSetUpAgain()
    {
        var state = State("ours-1");
        state.FeedsV4Id = null;

        GatewayBlocklist.Verify(state, [P("ours-1")]).Should().Be("Run Set Up again to add the public block lists to the gateway.");
    }
}

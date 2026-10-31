using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

/// <summary>
/// The per-zone gateway policies follow the site's zones: a zone added or removed anywhere (UniFi Network, Set Up
/// Zones, its Roll Back) leaves them short or stale, and the blocking cycle re-runs the idempotent Set Up.
/// </summary>
public class GatewayZoneCoverageTests
{
    private static UniFiFirewallZone Z(string id, string key = "") => new() { Id = id, Name = id, ZoneKey = key };

    private static readonly List<UniFiFirewallZone> Zones =
        [Z("ext", FirewallZoneKeys.External), Z("gw", FirewallZoneKeys.Gateway), Z("int", FirewallZoneKeys.Internal), Z("iot")];

    private static GatewayBlocklistState State(string[] inbound, string[] outbound) => new()
    {
        Policies = inbound.SelectMany(z => new[] { new GatewayBlockPolicyRef($"in-{z}-4", z, "IPV4"), new GatewayBlockPolicyRef($"in-{z}-6", z, "IPV6") }).ToList(),
        OutboundPolicies = outbound.SelectMany(z => new[] { new GatewayBlockPolicyRef($"out-{z}-4", z, "IPV4"), new GatewayBlockPolicyRef($"out-{z}-6", z, "IPV6") }).ToList(),
    };

    [Fact]
    public void MatchingZones_NeedNothing()
    {
        // Inbound covers every zone but External (the Gateway included); outbound every zone but External and Gateway.
        GatewayBlocklist.Uncovered(State(["gw", "int", "iot"], ["int", "iot"]), Zones, "ext").Should().Be((false, false));
    }

    [Fact]
    public void AZoneAdded_NeedsBoth()
    {
        var zones = Zones.Append(Z("cameras")).ToList();

        GatewayBlocklist.Uncovered(State(["gw", "int", "iot"], ["int", "iot"]), zones, "ext").Should().Be((true, true));
    }

    [Fact]
    public void AZoneRemoved_NeedsBoth()
    {
        var zones = Zones.Where(z => z.Id != "iot").ToList();

        GatewayBlocklist.Uncovered(State(["gw", "int", "iot"], ["int", "iot"]), zones, "ext").Should().Be((true, true));
    }

    [Fact]
    public void OutboundNotSetUp_IsNeverAdded()
    {
        var zones = Zones.Append(Z("cameras")).ToList();

        GatewayBlocklist.Uncovered(State(["gw", "int", "iot"], []), zones, "ext").Should().Be((true, false));
    }

    [Fact]
    public void ZoneIdsCompareWithoutCase()
    {
        GatewayBlocklist.Uncovered(State(["GW", "INT", "IOT"], ["INT", "IOT"]), Zones, "ext").Should().Be((false, false));
    }

    [Fact]
    public void ZoneChanges_NameTheZoneAdded()
    {
        var zones = Zones.Append(Z("Test Zone")).ToList();

        GatewayBlocklist.ZoneChanges(State(["gw", "int", "iot"], ["int", "iot"]), zones, "ext")
            .Should().BeEquivalentTo((Added: new List<string> { "Test Zone" }, Gone: new List<string>()));
    }

    [Fact]
    public void ZoneChanges_ListTheZoneGone()
    {
        var zones = Zones.Where(z => z.Id != "iot").ToList();

        var (added, gone) = GatewayBlocklist.ZoneChanges(State(["gw", "int", "iot"], ["int", "iot"]), zones, "ext");

        added.Should().BeEmpty();
        gone.Should().Equal(["iot"]);
    }

    [Fact]
    public void ZoneChanges_ForAReKeyedZone_LookLikeBoth()
    {
        // A Network app upgrade re-keyed the zone: its policies are already there under the new ID, so Set Up creates
        // nothing and the check logs a record refresh, not a change.
        var zones = Zones.Select(z => z.Id == "iot" ? Z("iot-uuid") : z).ToList();

        var (added, gone) = GatewayBlocklist.ZoneChanges(State(["gw", "int", "iot"], ["int", "iot"]), zones, "ext");

        added.Should().Equal(["iot-uuid"]);
        gone.Should().Equal(["iot"]);
    }

    [Fact]
    public void ZoneChanges_NoneWhenTheyMatch()
    {
        var (added, gone) = GatewayBlocklist.ZoneChanges(State(["gw", "int", "iot"], ["int", "iot"]), Zones, "ext");

        added.Should().BeEmpty();
        gone.Should().BeEmpty();
    }

    private static UniFiFirewallPolicy Policy(string id, long index, string name, string src, string dst) => new()
    {
        Id = id, Index = index, Name = name,
        Source = new UniFiFirewallPolicyEndpoint { ZoneId = src },
        Destination = new UniFiFirewallPolicyEndpoint { ZoneId = dst },
    };

    [Fact]
    public void ExceptionIds_IncludeExceptionsTheRecordLost()
    {
        // The record still holds the ID from before the Network 11 upgrade re-keyed it.
        var state = new GatewayBlocklistState { ExceptionPolicies = [new GatewayExceptionPolicyRef("old-objectid", "e1:in:vpn:4")] };
        var current = new List<UniFiFirewallPolicy>
        {
            Policy("exc-uuid", 10003, "Net. Opt. Exception", "ext", "vpn"),
            Policy("user-allow", 10006, "[VPN] Allow Admin", "ext", "vpn"),
        };

        GatewayBlocklist.ExceptionIds(state, current).Should().BeEquivalentTo(["old-objectid", "exc-uuid"]);
    }

    [Fact]
    public void SetUpOrder_KeepsAnUnrecordedExceptionAheadOfThePublicLists()
    {
        // External to Vpn after the drift: the exception pushed behind the public lists, where it never matches.
        var current = new List<UniFiFirewallPolicy>
        {
            Policy("bl4", 10001, "Net. Opt. Blocklist", "ext", "vpn"),
            Policy("bl6", 10002, "Net. Opt. Blocklist", "ext", "vpn"),
            Policy("pl4", 10003, "Net. Opt. Public Lists", "ext", "vpn"),
            Policy("pl6", 10004, "Net. Opt. Public Lists", "ext", "vpn"),
            Policy("exc-uuid", 10005, "Net. Opt. Exception", "ext", "vpn"),
        };
        var state = new GatewayBlocklistState { ExceptionPolicies = [new GatewayExceptionPolicyRef("old-objectid", "e1:in:vpn:4")] };
        var own = new HashSet<string> { "bl4", "bl6" };
        var ids = new HashSet<string> { "bl4", "bl6", "pl4", "pl6" };

        var order = GatewayExceptionPolicies.Order(current, "ext", "vpn", own, GatewayBlocklist.ExceptionIds(state, current), ids);

        order!.Value.Before.Should().Equal("bl4", "bl6", "exc-uuid", "pl4", "pl6");
    }

    [Fact]
    public void UntrackedExceptions_AreTheLeftoversOfAStaleRecord()
    {
        // Main site after the exception re-sync: the fresh policies are tracked, the old ones (the Baseline's) are not.
        var live = new List<UniFiFirewallPolicy>
        {
            Policy("fresh-in", 10002, "Net. Opt. Exception", "ext", "vpn"),
            Policy("old-in", 10005, "Net. Opt. Exception", "ext", "vpn"),
            Policy("fresh-out", 10000, "Net. Opt. Exception", "vpn", "ext"),
            Policy("old-out", 10003, "Net. Opt. Exception", "vpn", "ext"),
            Policy("lists", 10003, "Net. Opt. Public Lists", "ext", "vpn"),
            Policy("user", 10006, "[VPN] Allow Exception Host", "ext", "vpn"),
        };
        var tracked = new[] { new GatewayExceptionPolicyRef("fresh-in", "e1:in:vpn:4"), new GatewayExceptionPolicyRef("FRESH-OUT", "e1:out:vpn:4") };

        GatewayBlocklist.UntrackedExceptions(live, tracked).Should().Equal("old-in", "old-out");
    }

    [Fact]
    public void UntrackedExceptions_NeverIncludePredefinedOrOtherPolicies()
    {
        var live = new List<UniFiFirewallPolicy>
        {
            new() { Id = "pre", Name = "Net. Opt. Exception", Predefined = true },
            Policy("bl", 10001, "Net. Opt. Blocklist", "ext", "vpn"),
        };

        GatewayBlocklist.UntrackedExceptions(live, []).Should().BeEmpty();
    }
}

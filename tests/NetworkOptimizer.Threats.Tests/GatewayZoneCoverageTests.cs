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
}

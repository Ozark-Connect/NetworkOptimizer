using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests;

public class ExternalZoneDeterminationTests
{
    private static readonly FirewallZoneLookup Zones = new(
    [
        new UniFiFirewallZone { Id = "zone-internal", ZoneKey = "internal", Name = "Internal" },
        new UniFiFirewallZone { Id = "zone-external", ZoneKey = "external", Name = "External" },
        new UniFiFirewallZone { Id = "zone-gateway", ZoneKey = "gateway", Name = "Gateway" },
    ]);

    private static string? Determine(string? wanZone, FirewallZoneLookup zones) => ConfigAuditEngine.DetermineExternalZoneId(
        [new UniFiNetworkConfig { Id = "n-wan", Name = "Internet", Purpose = "wan", FirewallZoneId = wanZone }], null, zones, NullLogger.Instance);

    [Fact]
    public void WanZoneThatExists_IsUsed()
    {
        Determine("zone-external", Zones).Should().Be("zone-external");
    }

    [Fact]
    public void WanZoneThatMatchesNoZone_UsesTheExternalZone()
    {
        Determine("zone-gone", Zones).Should().Be("zone-external");
    }

    [Fact]
    public void WithoutZoneData_TheWanZoneStands()
    {
        Determine("zone-gone", new FirewallZoneLookup(null)).Should().Be("zone-gone");
    }

    [Fact]
    public void LegacyWanWithoutAZone_UsesTheLegacyId()
    {
        Determine(null, Zones).Should().Be(FirewallRuleParser.LegacyExternalZoneId);
    }
}

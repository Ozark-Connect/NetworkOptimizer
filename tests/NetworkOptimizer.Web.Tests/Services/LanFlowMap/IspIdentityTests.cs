using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.LanFlowMap;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.LanFlowMap;

/// <summary>
/// Which ASN a WAN globe carries, and so which ISP logo the maps draw: the first enabled access
/// hop with an ASN, then the gateway's geo-IP lookup, then a paused access hop.
/// </summary>
public class IspIdentityTests
{
    // A gateway's geo_info as stat/device returns it: one entry per WAN network group.
    private static readonly UniFiDeviceResponse Gateway = JsonSerializer.Deserialize<UniFiDeviceResponse>("""
        {
          "mac": "aa:bb:cc:00:00:01", "type": "uxg", "model": "UXGA6AA",
          "geo_info": {
            "WAN": { "address": "203.0.113.10", "asn": 64500, "isp_name": "Fiber ISP", "isp_organization": "FIBER ISP INC" },
            "WAN2": { "address": "198.51.100.20", "asn": "64501", "isp_name": "Satellite ISP" },
            "WAN3": { "address": "192.0.2.30", "asn": 0, "isp_name": "Unknown ISP" }
          }
        }
        """)!;

    private static AccessIspCloud Access(int? asn = null, string? name = null, int? pausedAsn = null, string? pausedName = null) =>
        new() { AsnNumber = asn, AsnName = name, PausedAsnNumber = pausedAsn, PausedAsnName = pausedName };

    [Fact]
    public void GeoInfo_ReadsTheAsnPerWan_NumberOrString()
    {
        Gateway.GeoInfo!["WAN"].Asn.Should().Be(64500);
        Gateway.GeoInfo["WAN2"].Asn.Should().Be(64501);
        Gateway.GeoInfo["WAN2"].AnyName.Should().Be("Satellite ISP");
    }

    [Fact]
    public void AnEnabledAccessHopWins()
    {
        LanFlowMapService.ResolveIspIdentity(Access(64496, "Traced ISP", pausedAsn: 64497), Gateway, "wan")
            .Should().Be(((int?)64496, "Traced ISP"));
    }

    [Fact]
    public void WithNoTracedAsn_TheGatewaysGeoAsnIsUsed()
    {
        LanFlowMapService.ResolveIspIdentity(Access(pausedAsn: 64497, pausedName: "Paused ISP"), Gateway, "wan2")
            .Should().Be(((int?)64501, "Satellite ISP"), "the gateway's lookup ranks ahead of a paused hop");
    }

    [Fact]
    public void WanOneMapsToTheWanKey()
    {
        LanFlowMapService.ResolveIspIdentity(Access(), Gateway, "wan").Asn.Should().Be(64500);
    }

    [Fact]
    public void APausedHopIsTheLastResort()
    {
        LanFlowMapService.ResolveIspIdentity(Access(pausedAsn: 64497, pausedName: "Paused ISP"), Gateway, "wan3")
            .Should().Be(((int?)64497, "Paused ISP"), "a geo asn of 0 means unknown");
        LanFlowMapService.ResolveIspIdentity(Access(pausedAsn: 64497, pausedName: "Paused ISP"), null, "wan")
            .Should().Be(((int?)64497, "Paused ISP"));
    }

    [Fact]
    public void NoAsnAnywhere_LeavesTheGlobeWithoutOne()
    {
        LanFlowMapService.ResolveIspIdentity(Access(), null, "wan").Should().Be(((int?)null, (string?)null));
        LanFlowMapService.ResolveIspIdentity(Access(), Gateway, "wan3").Should().Be(((int?)null, "Unknown ISP"));
    }
}

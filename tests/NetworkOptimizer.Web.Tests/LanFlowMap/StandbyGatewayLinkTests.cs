using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.LanFlowMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.LanFlowMap;

/// <summary>
/// A Shadow Mode standby's uplink is its own WAN side, so the map hangs it off the active gateway
/// instead of drawing a second, unconnected gateway.
/// </summary>
public class StandbyGatewayLinkTests
{
    private const string A = "aa:bb:cc:00:00:01";
    private const string B = "aa:bb:cc:00:00:02";

    private static DiscoveredDevice Gateway(string mac, string? vrrp = null, string? mode = null, bool appMode = false) => new()
    {
        Mac = mac,
        Type = DeviceType.Gateway,
        HardwareType = DeviceType.Gateway,
        VrrpState = vrrp,
        DeviceModeOverride = mode,
        ApplicationModeEnabled = appMode,
        State = 1,
    };

    [Fact]
    public void Standby_HangsOffTheActiveGateway()
    {
        var devices = new List<DiscoveredDevice> { Gateway(A), Gateway(B, "backup", GatewayHa.ShadowModeOverride) };

        var links = LanFlowMapService.StandbyGatewayLinks(devices, []);

        links.Should().ContainSingle();
        links[0].FromNodeId.Should().Be("dev-" + A);
        links[0].ToNodeId.Should().Be("dev-" + B);
        links[0].Kind.Should().Be(LanLinkKind.Uplink);
        links[0].PortKey.Should().BeNull();
    }

    [Fact]
    public void AfterFailover_ThePrimaryHangsOffTheShadow()
    {
        var devices = new List<DiscoveredDevice> { Gateway(A, "backup"), Gateway(B, "master", GatewayHa.ShadowModeOverride) };

        var links = LanFlowMapService.StandbyGatewayLinks(devices, []);

        links.Should().ContainSingle(l => l.FromNodeId == "dev-" + B && l.ToNodeId == "dev-" + A);
    }

    [Fact]
    public void AlreadyLinked_LoneGateway_AndApplicationModeUdm_GetNoEdge()
    {
        var pair = new List<DiscoveredDevice> { Gateway(A), Gateway(B, "backup", GatewayHa.ShadowModeOverride) };
        LanFlowMapService.StandbyGatewayLinks(pair, [new LanLink { Id = "x", FromNodeId = "dev-sw", ToNodeId = "dev-" + B, Kind = LanLinkKind.Uplink }])
            .Should().BeEmpty();

        LanFlowMapService.StandbyGatewayLinks([Gateway(A)], []).Should().BeEmpty();

        LanFlowMapService.StandbyGatewayLinks([Gateway(A), Gateway(B, appMode: true)], []).Should().BeEmpty();
    }
}

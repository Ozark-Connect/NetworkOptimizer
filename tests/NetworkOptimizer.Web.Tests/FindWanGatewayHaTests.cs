using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The WAN lookups read the VRRP master's wan objects: the primary normally, the Managed Shadow
/// Gateway after a failover. UniFi copies the primary's overrides onto the shadow, so both carry
/// the same WANs and only the VRRP state tells them apart.
/// </summary>
public class FindWanGatewayHaTests
{
    private static string Unit(string mac, string? mode, string? vrrp) =>
        "{ \"type\": \"uxg\", \"model\": \"UXGENT\", \"mac\": \"" + mac + "\""
        + (mode == null ? "" : ", \"device_mode_override\": \"" + mode + "\"")
        + (vrrp == null ? "" : ", \"vrrp\": { \"state\": \"" + vrrp + "\" }")
        + ", \"wan1\": { \"uplink_ifname\": \"eth4\", \"ifname\": \"eth4\" } }";

    private static List<UniFiDeviceResponse> Devices(params string[] devices) =>
        JsonSerializer.Deserialize<List<UniFiDeviceResponse>>($"[{string.Join(",", devices)}]")!;

    [Fact]
    public void Normal_ReadsThePrimary()
    {
        var devices = Devices(Unit("aa:bb:cc:00:00:02", "managed_shadow_gateway", "backup"), Unit("aa:bb:cc:00:00:01", null, null));

        UniFiConnectionService.FindWanGateway(devices)!.Mac.Should().Be("aa:bb:cc:00:00:01");
    }

    [Fact]
    public void FailedOver_ReadsTheShadow()
    {
        var devices = Devices(Unit("aa:bb:cc:00:00:01", null, "backup"), Unit("aa:bb:cc:00:00:02", "managed_shadow_gateway", "master"));

        UniFiConnectionService.FindWanGateway(devices)!.Mac.Should().Be("aa:bb:cc:00:00:02");
    }

    [Fact]
    public void ChangeControl_RecognizesTheShadow()
    {
        var shadow = System.Text.Json.Nodes.JsonNode.Parse(Unit("aa:bb:cc:00:00:02", "managed_shadow_gateway", "backup"))!.AsObject();
        var primary = System.Text.Json.Nodes.JsonNode.Parse(Unit("aa:bb:cc:00:00:01", null, null))!.AsObject();

        ConfigCollections.IsGateway(shadow).Should().BeTrue();
        ConfigCollections.IsShadowGateway(shadow).Should().BeTrue();
        ConfigCollections.IsShadowGateway(primary).Should().BeFalse();
    }
}

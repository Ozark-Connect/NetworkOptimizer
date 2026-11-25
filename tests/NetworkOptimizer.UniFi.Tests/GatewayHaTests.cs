using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// UniFi's gateway rules (ace.jar, Network 11.1.58) on device JSON shaped like stat/device: a
/// Managed Shadow Gateway pair before and after a failover, after a promote, a per-network VRRP
/// secondary, and a UDM in application mode.
/// </summary>
public class GatewayHaTests
{
    private const string PrimaryMac = "aa:bb:cc:00:00:01";
    private const string ShadowMac = "aa:bb:cc:00:00:02";

    private static UniFiDeviceResponse Parse(string json) =>
        JsonSerializer.Deserialize<UniFiDeviceResponse>(json)!;

    // UniFi copies the primary's name onto the shadow, so the two read the same.
    private static string Vrrp(string? state) =>
        state == null ? "" : ", \"vrrp\": { \"state\": \"" + state + "\", \"lastTransition\": 1700000000 }";

    private static UniFiDeviceResponse Primary(string? vrrpState = null) => Parse(
        "{ \"mac\": \"" + PrimaryMac + "\", \"type\": \"uxg\", \"model\": \"UXGENT\", \"name\": \"Gateway\", \"state\": 1"
        + Vrrp(vrrpState) + " }");

    private static UniFiDeviceResponse Shadow(string vrrpState, bool labelled = true) => Parse(
        "{ \"mac\": \"" + ShadowMac + "\", \"type\": \"uxg\", \"model\": \"UXGENT\", \"name\": \"Gateway\", \"state\": 1"
        + (labelled ? ", \"device_mode_override\": \"managed_shadow_gateway\"" : "")
        + Vrrp(vrrpState) + " }");

    private static UniFiDeviceResponse Switch() => Parse("""
        { "mac": "aa:bb:cc:00:00:10", "type": "usw", "model": "US24", "name": "Switch", "state": 1 }
        """);

    [Fact]
    public void Deserializes_HaFields()
    {
        var d = Parse("""
            { "mac": "aa:bb:cc:00:00:03", "type": "udm", "model": "UDMPRO",
              "vrrp": { "state": "backup", "lastTransition": "1700000000" },
              "gateway_vrrp_mode": "secondary", "application_mode_enabled": "true" }
            """);

        d.Vrrp!.State.Should().Be("backup");
        d.Vrrp.LastTransition.Should().Be(1700000000);
        d.GatewayVrrpMode.Should().Be("secondary");
        d.ApplicationModeEnabled.Should().BeTrue();
    }

    [Fact]
    public void Deserializes_UnexpectedVrrpShape_WithoutFailingTheDevice()
    {
        var d = Parse("""{ "mac": "aa:bb:cc:00:00:03", "type": "uxg", "model": "UXGENT", "vrrp": "master" }""");

        d.Mac.Should().Be("aa:bb:cc:00:00:03");
        d.Vrrp.Should().BeNull();
    }

    [Fact]
    public void Pair_Normal_ActiveIsPrimary()
    {
        var devices = new[] { Shadow("backup"), Primary(), Switch() };

        GatewayHa.Active(devices)!.Mac.Should().Be(PrimaryMac);
        GatewayHa.Primary(devices)!.Mac.Should().Be(PrimaryMac);
        GatewayHa.IsStandby(devices[0], devices).Should().BeTrue();
        GatewayHa.IsStandby(devices[1], devices).Should().BeFalse();
        GatewayHa.IsUnreachableStandby(devices[0], devices).Should().BeTrue();
        GatewayHa.IsUnreachableStandby(devices[1], devices).Should().BeFalse();
    }

    [Fact]
    public void Pair_FailedOver_ActiveIsShadow_PrimaryKeepsConfig()
    {
        var devices = new[] { Primary("backup"), Shadow("master") };

        GatewayHa.Active(devices)!.Mac.Should().Be(ShadowMac);
        GatewayHa.Primary(devices)!.Mac.Should().Be(PrimaryMac);
        GatewayHa.IsStandby(devices[0], devices).Should().BeTrue();
        GatewayHa.IsStandby(devices[1], devices).Should().BeFalse();
        GatewayHa.IsUnreachableStandby(devices[0], devices).Should().BeTrue();
    }

    [Fact]
    public void Pair_Roles()
    {
        var devices = new[] { Primary(), Shadow("backup"), Switch() };

        GatewayHa.RoleOf(devices[0], devices).Should().Be(GatewayHaRole.Primary);
        GatewayHa.RoleOf(devices[1], devices).Should().Be(GatewayHaRole.Shadow);
        GatewayHa.RoleOf(devices[2], devices).Should().Be(GatewayHaRole.None);
    }

    [Fact]
    public void Promoted_LabelCleared_IsALoneGateway()
    {
        // Promote forgets the primary; UniFi then clears the shadow's device_mode_override.
        var promoted = Shadow("master", labelled: false);
        var devices = new[] { promoted, Switch() };

        GatewayHa.Active(devices).Should().BeSameAs(promoted);
        GatewayHa.Primary(devices).Should().BeSameAs(promoted);
        GatewayHa.RoleOf(promoted, devices).Should().Be(GatewayHaRole.None);
        GatewayHa.IsStandby(promoted, devices).Should().BeFalse();
    }

    [Fact]
    public void ShadowOnly_PrimaryForgottenBeforeLabelCleared_IsStillPicked()
    {
        var shadow = Shadow("backup");

        GatewayHa.Active(new[] { shadow }).Should().BeSameAs(shadow);
        GatewayHa.Primary(new[] { shadow }).Should().BeSameAs(shadow);
    }

    [Fact]
    public void LoneGateway_Unchanged()
    {
        var gateway = Primary();
        var devices = new[] { Switch(), gateway };

        GatewayHa.Active(devices).Should().BeSameAs(gateway);
        GatewayHa.Primary(devices).Should().BeSameAs(gateway);
        GatewayHa.RoleOf(gateway, devices).Should().Be(GatewayHaRole.None);
        GatewayHa.IsStandby(gateway, devices).Should().BeFalse();
        GatewayHa.IsUnreachableStandby(gateway, devices).Should().BeFalse();
    }

    [Theory]
    [InlineData("UDMPRO")]
    [InlineData("UDMPROSE")]
    [InlineData("UDMPROMAX")]
    [InlineData("UDMEA4C")]
    [InlineData("UDMENT")]
    [InlineData("EFGCORE")]
    public void ConsoleShadowMode_BothListed_TheVrrpMasterWins(string model)
    {
        // Console Shadow Mode carries vrrp.state but no shadow label.
        UniFiDeviceResponse Udm(string mac, string state) => Parse(
            "{ \"mac\": \"" + mac + "\", \"type\": \"udm\", \"model\": \"" + model + "\", \"state\": 1" + Vrrp(state) + " }");
        var backup = Udm("aa:bb:cc:00:00:01", "backup");
        var master = Udm("aa:bb:cc:00:00:02", "master");
        var devices = new[] { backup, master };

        GatewayHa.Active(devices).Should().BeSameAs(master);
        GatewayHa.IsStandby(backup, devices).Should().BeTrue();
        GatewayHa.IsStandby(master, devices).Should().BeFalse();
        GatewayHa.IsUnreachableStandby(backup, devices).Should().BeTrue();
        GatewayHa.IsUnreachableStandby(master, devices).Should().BeFalse();
    }

    [Fact]
    public void ConsoleShadowMode_Takeover_StaleOfflineRowNeverWins()
    {
        // After a takeover the live row is the shadow's own; the failed primary's row may stay
        // listed, offline, with its last VRRP state still reading master.
        var stale = Parse("{ \"mac\": \"aa:bb:cc:00:00:01\", \"type\": \"udm\", \"model\": \"UDMPRO\", \"state\": 0" + Vrrp("master") + " }");
        var live = Parse("{ \"mac\": \"aa:bb:cc:00:00:02\", \"type\": \"udm\", \"model\": \"UDMPRO\", \"state\": 1" + Vrrp("master") + " }");
        var devices = new[] { stale, live };

        GatewayHa.Active(devices).Should().BeSameAs(live);
        GatewayHa.IsStandby(live, devices).Should().BeFalse();
        GatewayHa.IsUnreachableStandby(live, devices).Should().BeFalse();
    }

    [Fact]
    public void ConsoleShadowMode_OnlyTheLocalUdmListed_IsALoneGateway()
    {
        var udm = Parse("{ \"mac\": \"aa:bb:cc:00:00:01\", \"type\": \"udm\", \"model\": \"UDMPRO\"" + Vrrp("backup") + " }");
        var devices = new[] { udm, Switch() };

        GatewayHa.Active(devices).Should().BeSameAs(udm);
        GatewayHa.IsStandby(udm, devices).Should().BeFalse("nothing else holds master");
        GatewayHa.IsUnreachableStandby(udm, devices).Should().BeFalse();
    }

    [Fact]
    public void VrrpSecondary_IsNeverTheSiteGateway_AndIsReachable()
    {
        var primary = Parse("""{ "mac": "aa:bb:cc:00:00:01", "type": "uxg", "model": "UXGPRO", "gateway_vrrp_mode": "primary" }""");
        var secondary = Parse("""{ "mac": "aa:bb:cc:00:00:02", "type": "uxg", "model": "UXGPRO", "gateway_vrrp_mode": "secondary" }""");
        var devices = new[] { secondary, primary };

        secondary.DeviceType.Should().Be(DeviceType.Gateway, "the unit keeps its gateway role and icon");
        GatewayHa.IsGateway(secondary).Should().BeFalse();
        GatewayHa.Active(devices).Should().BeSameAs(primary);
        GatewayHa.Primary(devices).Should().BeSameAs(primary);
        GatewayHa.IsStandby(secondary, devices).Should().BeTrue();
        GatewayHa.IsUnreachableStandby(secondary, devices).Should().BeFalse("it holds its own LAN address");
    }

    [Fact]
    public void UdmInApplicationMode_IsNotTheSiteGateway()
    {
        var console = Parse("""{ "mac": "aa:bb:cc:00:00:01", "type": "udm", "model": "UDMPRO", "application_mode_enabled": true }""");
        var gateway = Parse("""{ "mac": "aa:bb:cc:00:00:02", "type": "uxg", "model": "UXGPRO" }""");

        GatewayHa.Active(new[] { console, gateway }).Should().BeSameAs(gateway);
        GatewayHa.Active(new[] { console }).Should().BeNull();
    }

    [Fact]
    public void FromJson_ReadsTheSameRules()
    {
        using var doc = JsonDocument.Parse($$"""
            [ { "mac": "{{PrimaryMac}}", "type": "uxg", "model": "UXGENT", "state": 1 },
              { "mac": "{{ShadowMac}}", "type": "uxg", "model": "UXGENT", "state": 1,
                "device_mode_override": "managed_shadow_gateway", "vrrp": { "state": "master" } } ]
            """);
        var facts = doc.RootElement.EnumerateArray().Select(GatewayHa.FromJson).ToList();

        GatewayHa.IsShadow(facts[1]).Should().BeTrue();
        GatewayHa.IsStandby(facts[0], facts).Should().BeTrue();
        GatewayHa.IsStandby(facts[1], facts).Should().BeFalse();
    }

    [Fact]
    public void LanAddress_FromNetwork_DerivesUnitAddresses()
    {
        var lan = GatewayLanAddress.FromNetwork(new UniFiNetworkConfig { IpSubnet = "192.0.2.1/24" })!;

        lan.Vip.Should().Be("192.0.2.1");
        lan.AddressFor(null).Should().Be("192.0.2.1");
        lan.AddressFor("primary").Should().Be("192.0.2.2");
        lan.AddressFor("secondary").Should().Be("192.0.2.3");
    }

    [Fact]
    public void LanAddress_FromNetwork_PrefersConfiguredUnitAddresses()
    {
        var lan = GatewayLanAddress.FromNetwork(new UniFiNetworkConfig
        {
            IpSubnet = "192.0.2.1/24",
            DhcpdGateway = "192.0.2.254",
            VrrpIpSubnetGw1 = "192.0.2.10/24",
            VrrpIpSubnetGw2 = "192.0.2.11/24",
        })!;

        lan.Vip.Should().Be("192.0.2.254");
        lan.AddressFor("PRIMARY").Should().Be("192.0.2.10");
        lan.AddressFor("secondary").Should().Be("192.0.2.11");
    }

    [Fact]
    public void LanAddress_FromNetwork_NoAddress_IsNull()
    {
        GatewayLanAddress.FromNetwork(new UniFiNetworkConfig()).Should().BeNull();
        GatewayLanAddress.FromNetwork(null).Should().BeNull();
    }
}

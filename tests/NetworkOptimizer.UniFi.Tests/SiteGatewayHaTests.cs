using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The discovered-device gateway picks on HA sites: the active unit for traffic, the primary for
/// config, and the LAN address each unit is reached at.
/// </summary>
public class SiteGatewayHaTests
{
    private static readonly GatewayLanAddress Lan = new("192.0.2.1", "192.0.2.2", "192.0.2.3");

    private static DiscoveredDevice Gateway(string mac, string? mode = null, string? vrrp = null,
        string? vrrpMode = null, bool appMode = false) => new()
    {
        Mac = mac,
        Name = "Gateway",
        Type = DeviceType.Gateway,
        HardwareType = DeviceType.Gateway,
        DeviceModeOverride = mode,
        VrrpState = vrrp,
        GatewayVrrpMode = vrrpMode,
        ApplicationModeEnabled = appMode,
        State = 1,
        IpAddress = "203.0.113.10",
        LanIpAddress = "192.0.2.1",
    };

    private static DiscoveredDevice Shadow(string vrrp) =>
        Gateway("aa:bb:cc:00:00:02", GatewayHa.ShadowModeOverride, vrrp);

    [Fact]
    public void SiteGateway_FollowsTheVrrpMaster_PrimaryGatewayDoesNot()
    {
        var primary = Gateway("aa:bb:cc:00:00:01");

        var normal = new List<DiscoveredDevice> { Shadow("backup"), primary };
        UniFiDiscovery.SiteGateway(normal).Should().BeSameAs(primary);
        UniFiDiscovery.PrimaryGateway(normal).Should().BeSameAs(primary);

        var failedOver = new List<DiscoveredDevice> { primary, Shadow("master") };
        UniFiDiscovery.SiteGateway(failedOver)!.Mac.Should().Be("aa:bb:cc:00:00:02");
        UniFiDiscovery.PrimaryGateway(failedOver).Should().BeSameAs(primary);
    }

    [Fact]
    public void SiteGateways_StillListsBothMembers()
    {
        var devices = new List<DiscoveredDevice> { Gateway("aa:bb:cc:00:00:01"), Shadow("backup") };

        UniFiDiscovery.SiteGateways(devices).Should().HaveCount(2);
    }

    [Fact]
    public void SiteGateway_NeverPicksAnApplicationModeUdm()
    {
        var console = Gateway("aa:bb:cc:00:00:01", appMode: true);

        UniFiDiscovery.SiteGateway(new[] { console }).Should().BeNull();
        UniFiDiscovery.PrimaryGateway(new[] { console }).Should().BeNull();
        console.Type.Should().Be(DeviceType.Gateway, "it keeps its gateway role and icon");
    }

    [Fact]
    public void ApplyGatewayHa_SetsRolesAndLanAddresses()
    {
        var primary = Gateway("aa:bb:cc:00:00:01");
        var shadow = Shadow("backup");
        var devices = new List<DiscoveredDevice> { primary, shadow };

        UniFiDiscovery.ApplyGatewayHa(devices, Lan);

        primary.HaRole.Should().Be(GatewayHaRole.Primary);
        shadow.HaRole.Should().Be(GatewayHaRole.Shadow);
        primary.LanIpAddress.Should().Be("192.0.2.1");
        shadow.LanIpAddress.Should().BeNull("no LAN address reaches the backup, and the VIP reaches the master");
        primary.IsActiveGateway.Should().BeTrue();
        shadow.IsActiveGateway.Should().BeFalse();
    }

    [Fact]
    public void ApplyGatewayHa_AfterFailover_TheBackupPrimaryLosesItsLanAddress()
    {
        var primary = Gateway("aa:bb:cc:00:00:01", vrrp: "backup");
        var shadow = Shadow("master");
        var devices = new List<DiscoveredDevice> { primary, shadow };

        UniFiDiscovery.ApplyGatewayHa(devices, Lan);

        primary.LanIpAddress.Should().BeNull();
        shadow.LanIpAddress.Should().Be("192.0.2.1");
        primary.HaRole.Should().Be(GatewayHaRole.Primary, "a failover changes only the VRRP state");
        primary.IsActiveGateway.Should().BeFalse("UniFi's per-device rule passes the primary, the site pick does not");
        shadow.IsActiveGateway.Should().BeTrue();
    }

    [Fact]
    public void ApplyGatewayHa_PerNetworkVrrpUnits_TakeTheirOwnAddresses()
    {
        var primary = Gateway("aa:bb:cc:00:00:01", vrrpMode: "primary");
        var secondary = Gateway("aa:bb:cc:00:00:02", vrrpMode: "secondary");
        var devices = new List<DiscoveredDevice> { primary, secondary };

        UniFiDiscovery.ApplyGatewayHa(devices, Lan);

        primary.LanIpAddress.Should().Be("192.0.2.2");
        secondary.LanIpAddress.Should().Be("192.0.2.3");
        UniFiDiscovery.SiteGateway(devices).Should().BeSameAs(primary);
    }

    [Fact]
    public void ApplyGatewayHa_ApplicationModeUdm_KeepsItsOwnAddress()
    {
        var console = Gateway("aa:bb:cc:00:00:01", appMode: true);

        UniFiDiscovery.ApplyGatewayHa(new List<DiscoveredDevice> { console }, Lan);

        console.LanIpAddress.Should().BeNull();
        console.DisplayIpAddress.Should().Be("203.0.113.10");
    }

    [Fact]
    public void ApplyGatewayHa_LoneGateway_Unchanged()
    {
        var gateway = Gateway("aa:bb:cc:00:00:01");

        UniFiDiscovery.ApplyGatewayHa(new List<DiscoveredDevice> { gateway }, Lan);

        gateway.HaRole.Should().Be(GatewayHaRole.None);
        gateway.LanIpAddress.Should().Be("192.0.2.1");
    }
}

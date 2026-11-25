using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Poll addresses on HA sites. Every LAN address of a Managed Shadow pair is a VIP on the master,
/// so the backup gets no address (a poll at the VIP would record the master's numbers under its
/// MAC). Per-network VRRP units are polled at their own addresses.
/// </summary>
public class SnmpDeviceRulesHaTests
{
    private static readonly GatewayLanAddress Lan = new("192.0.2.1", "192.0.2.2", "192.0.2.3");

    private static UniFiDeviceResponse Gateway(string mac, string? mode = null, string? vrrp = null,
        string? vrrpMode = null, bool? appMode = null, string ip = "203.0.113.10") => new()
    {
        Mac = mac,
        Type = "uxg",
        Model = "UXGENT",
        Ip = ip,
        DeviceModeOverride = mode,
        Vrrp = vrrp == null ? null : new VrrpStatus { State = vrrp },
        GatewayVrrpMode = vrrpMode,
        ApplicationModeEnabled = appMode,
    };

    [Fact]
    public void ShadowPair_MasterAtTheVip_BackupSkipped()
    {
        var primary = Gateway("aa:bb:cc:00:00:01");
        var shadow = Gateway("aa:bb:cc:00:00:02", GatewayHa.ShadowModeOverride, "backup");
        var devices = new[] { primary, shadow };

        SnmpDeviceRules.ResolveUnitPollAddress(primary, devices, Lan).Should().Be("192.0.2.1");
        SnmpDeviceRules.ResolveUnitPollAddress(shadow, devices, Lan).Should().BeEmpty();
    }

    [Fact]
    public void ShadowPair_AfterFailover_TheShadowTakesTheVip()
    {
        var primary = Gateway("aa:bb:cc:00:00:01", vrrp: "backup");
        var shadow = Gateway("aa:bb:cc:00:00:02", GatewayHa.ShadowModeOverride, "master");
        var devices = new[] { primary, shadow };

        SnmpDeviceRules.ResolveUnitPollAddress(primary, devices, Lan).Should().BeEmpty();
        SnmpDeviceRules.ResolveUnitPollAddress(shadow, devices, Lan).Should().Be("192.0.2.1");
    }

    [Fact]
    public void PerNetworkVrrp_EachUnitAtItsOwnAddress()
    {
        var primary = Gateway("aa:bb:cc:00:00:01", vrrpMode: "primary");
        var secondary = Gateway("aa:bb:cc:00:00:02", vrrpMode: "secondary");
        var devices = new[] { primary, secondary };

        SnmpDeviceRules.ResolveUnitPollAddress(primary, devices, Lan).Should().Be("192.0.2.2");
        SnmpDeviceRules.ResolveUnitPollAddress(secondary, devices, Lan).Should().Be("192.0.2.3");
    }

    [Fact]
    public void ApplicationModeUdm_KeepsItsOwnAddress()
    {
        var console = Gateway("aa:bb:cc:00:00:01", appMode: true, ip: "192.0.2.50");

        SnmpDeviceRules.ResolveUnitPollAddress(console, new[] { console }, Lan).Should().Be("192.0.2.50");
    }

    [Fact]
    public void StringOverload_KeepsTheVipForALoneGateway()
    {
        var gateway = Gateway("aa:bb:cc:00:00:01");

        SnmpDeviceRules.ResolvePollAddress(gateway, new[] { gateway }, "192.0.2.1").Should().Be("192.0.2.1");
    }
}

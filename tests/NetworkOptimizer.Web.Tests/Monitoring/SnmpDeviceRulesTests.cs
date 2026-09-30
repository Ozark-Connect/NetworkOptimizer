using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Poll addresses: only the site's gateway swaps its WAN IP for the LAN-side gateway IP. A
/// gateway-class console adopted as an AP keeps its own address (issue #1244).
/// </summary>
public class SnmpDeviceRulesTests
{
    private const string GatewayLanIp = "192.0.2.1";

    private static UniFiDeviceResponse Device(string mac, string type, string model, string ip, string? uplinkMac = null) =>
        new()
        {
            Mac = mac,
            Type = type,
            Model = model,
            Ip = ip,
            Uplink = uplinkMac == null ? null : new UplinkInfo { UplinkMac = uplinkMac },
        };

    [Fact]
    public void Gateway_UsesLanIp()
    {
        var gateway = Device("aa:bb:cc:00:00:01", "udm", "UDMPRO", "203.0.113.10", uplinkMac: "00:11:22:33:44:55");

        SnmpDeviceRules.ResolvePollAddress(gateway, new[] { gateway }, GatewayLanIp).Should().Be(GatewayLanIp);
    }

    [Fact]
    public void ExpressAdoptedAsAp_KeepsOwnIp()
    {
        var gateway = Device("aa:bb:cc:00:00:01", "udm", "UDMPRO", "203.0.113.10");
        var express = Device("aa:bb:cc:00:00:02", "udm", "UDMA69B", "192.0.2.20", uplinkMac: "AA:BB:CC:00:00:01");
        var devices = new[] { gateway, express };

        SnmpDeviceRules.IsSiteGateway(express, devices).Should().BeFalse();
        SnmpDeviceRules.ResolvePollAddress(express, devices, GatewayLanIp).Should().Be("192.0.2.20");
    }

    [Fact]
    public void ExpressAsGateway_UsesLanIp()
    {
        var express = Device("aa:bb:cc:00:00:02", "udm", "UDMA69B", "203.0.113.10", uplinkMac: "00:11:22:33:44:55");

        SnmpDeviceRules.ResolvePollAddress(express, new[] { express }, GatewayLanIp).Should().Be(GatewayLanIp);
    }

    [Fact]
    public void Gateway_NoLanIp_KeepsReportedIp()
    {
        var gateway = Device("aa:bb:cc:00:00:01", "udm", "UDMPRO", "203.0.113.10");

        SnmpDeviceRules.ResolvePollAddress(gateway, new[] { gateway }, null).Should().Be("203.0.113.10");
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// An Express (UX) adopted as an AP reports type "udm" with device_mode_override "mesh" and no
/// uplink_mac. Its role is AccessPoint, its hardware class stays Gateway, and LLDP names its
/// wired parent. Device shapes follow a real Express + UCG-Fiber stat/device pair, scrubbed.
/// </summary>
public class ExpressApModeTests
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";
    private const string ExpressMac = "aa:bb:cc:00:00:02";

    // Express in AP mode: uplink holds byte counters only; port 2 is the uplink, LLDP names the gateway.
    private const string ExpressJson = """
    {
      "_id": "express", "mac": "aa:bb:cc:00:00:02", "name": "Express", "type": "udm", "model": "UX",
      "shortname": "UX", "device_mode_override": "mesh", "is_access_point": true,
      "uplink": { "tx_bytes": 100, "rx_bytes": 200 },
      "port_table": [
        { "port_idx": 1, "name": "Port 1", "ifname": "eth0", "is_uplink": false, "up": true, "speed": 1000 },
        { "port_idx": 2, "name": "Port 2", "ifname": "eth1", "is_uplink": true, "up": true, "speed": 1000 }
      ],
      "lldp_table": [
        { "chassis_id": "02:00:00:00:00:99", "chassis_id_subtype": "mac", "is_wired": true, "local_port_idx": 1, "local_port_name": "eth0", "port_id": "02:00:00:00:00:99" },
        { "chassis_id": "aa:bb:cc:00:00:01", "chassis_id_subtype": "local", "is_wired": true, "local_port_idx": 2, "local_port_name": "eth1", "port_id": "aa:bb:cc:00:00:04" }
      ]
    }
    """;

    // The real gateway: no device_mode_override, WAN on port 7, the Express on port 4 by LLDP.
    private const string GatewayJson = """
    {
      "_id": "gateway", "mac": "aa:bb:cc:00:00:01", "name": "Gateway", "type": "udm", "model": "UDMA6A8",
      "shortname": "UCGF", "is_access_point": false,
      "uplink": { "port_idx": 7, "type": "wire", "up": true, "speed": 1000 },
      "port_table": [
        { "port_idx": 4, "name": "Unifi Express", "ifname": "eth3", "is_uplink": false, "up": true, "speed": 1000 },
        { "port_idx": 7, "name": "WAN", "ifname": "eth6", "is_uplink": true, "up": true, "speed": 1000 }
      ],
      "lldp_table": [
        { "chassis_id": "aa:bb:cc:00:00:02", "chassis_id_subtype": "local", "is_wired": true, "local_port_idx": 4, "local_port_name": "eth3", "port_id": "aa:bb:cc:00:00:03" }
      ]
    }
    """;

    private static UniFiDeviceResponse Parse(string json) => JsonSerializer.Deserialize<UniFiDeviceResponse>(json)!;

    #region ClassifyDeviceType

    [Theory]
    [InlineData("udm", "UX", "UX", "mesh", DeviceType.AccessPoint)]
    [InlineData("udm", "UX", "UX", null, DeviceType.Gateway)]
    [InlineData("udm", "UX", "UX", "managed_shadow_gateway", DeviceType.Gateway)]
    [InlineData("udm", "UDMA6A8", "UCGF", null, DeviceType.Gateway)]
    [InlineData("udm", "UXSP", null, "mesh", DeviceType.AccessPoint)]
    [InlineData("usw", "USMINI", "USW-Flex-Mini", "mesh", DeviceType.Switch)]
    [InlineData("uap", "U7PG2", "U6-Pro", "mesh", DeviceType.AccessPoint)]
    public void ClassifyDeviceType_UsesModeOnlyForGatewayClass(
        string type, string model, string? shortname, string? mode, DeviceType expected)
    {
        UniFiProductDatabase.ClassifyDeviceType(type, model, shortname, mode).Should().Be(expected);
    }

    #endregion

    #region Model

    [Fact]
    public void Express_RoleIsAccessPoint_HardwareIsGateway()
    {
        var express = Parse(ExpressJson);

        express.DeviceModeOverride.Should().Be("mesh");
        express.DeviceType.Should().Be(DeviceType.AccessPoint);
        express.HardwareType.Should().Be(DeviceType.Gateway);
        express.LldpTable.Should().HaveCount(2);
        express.LldpTable![1].LocalPortIdx.Should().Be(2);
        express.LldpTable[1].ChassisIdSubtype.Should().Be("local");
    }

    [Fact]
    public void Gateway_RoleAndHardwareAreGateway()
    {
        var gateway = Parse(GatewayJson);

        gateway.DeviceModeOverride.Should().BeNull();
        gateway.DeviceType.Should().Be(DeviceType.Gateway);
        gateway.HardwareType.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void DetermineDeviceType_ExpressByModeIsAccessPoint()
    {
        var express = Parse(ExpressJson);
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GatewayMac, ExpressMac };

        UniFiDiscovery.DetermineDeviceType(express, macs, NullLogger.Instance).Should().Be(DeviceType.AccessPoint);
        UniFiDiscovery.GetEffectiveDeviceType(express, [express, Parse(GatewayJson)]).Should().Be(DeviceType.AccessPoint);
    }

    #endregion

    #region FillWiredParentFromLldp

    [Fact]
    public void Fill_ExpressGetsGatewayAsWiredParent()
    {
        var express = Parse(ExpressJson);
        var gateway = Parse(GatewayJson);

        UniFiDiscovery.FillWiredParentFromLldp([express, gateway]);

        express.Uplink!.UplinkMac.Should().Be(GatewayMac);
        express.Uplink.UplinkRemotePort.Should().Be(4);
        express.Uplink.Type.Should().Be("wire");
        express.Uplink.PortIdx.Should().Be(2);
        express.Uplink.Up.Should().BeTrue();
        express.Uplink.Speed.Should().Be(1000);
        express.Uplink.TxBytes.Should().Be(100, "reported fields are kept");
    }

    [Fact]
    public void Fill_GatewayIsNeverFilled()
    {
        var express = Parse(ExpressJson);
        var gateway = Parse(GatewayJson);

        UniFiDiscovery.FillWiredParentFromLldp([express, gateway]);

        gateway.Uplink!.UplinkMac.Should().BeEmpty();
    }

    [Fact]
    public void Fill_CreatesUplinkWhenAbsent()
    {
        var express = Parse(ExpressJson);
        express.Uplink = null;

        UniFiDiscovery.FillWiredParentFromLldp([express, Parse(GatewayJson)]);

        express.Uplink.Should().NotBeNull();
        express.Uplink!.UplinkMac.Should().Be(GatewayMac);
    }

    [Fact]
    public void Fill_ReportedUplinkMacIsUntouched()
    {
        var express = Parse(ExpressJson);
        express.Uplink!.UplinkMac = "aa:bb:cc:00:00:09";
        express.Uplink.Type = "wire";

        UniFiDiscovery.FillWiredParentFromLldp([express, Parse(GatewayJson)]);

        express.Uplink.UplinkMac.Should().Be("aa:bb:cc:00:00:09");
        express.Uplink.UplinkRemotePort.Should().Be(0);
    }

    [Fact]
    public void Fill_NeighborThatIsNotAdoptedIsIgnored()
    {
        var express = Parse(ExpressJson);

        UniFiDiscovery.FillWiredParentFromLldp([express]);

        express.Uplink!.UplinkMac.Should().BeEmpty();
        express.Uplink.Type.Should().BeEmpty();
    }

    [Fact]
    public void Fill_WirelessLldpEntryIsIgnored()
    {
        var express = Parse(ExpressJson);
        express.LldpTable![1].IsWired = false;

        UniFiDiscovery.FillWiredParentFromLldp([express, Parse(GatewayJson)]);

        express.Uplink!.UplinkMac.Should().BeEmpty();
    }

    [Fact]
    public void Fill_MeshChildWithWirelessUplinkIsSkipped()
    {
        var express = Parse(ExpressJson);
        express.Uplink!.Type = "wireless";

        UniFiDiscovery.FillWiredParentFromLldp([express, Parse(GatewayJson)]);

        express.Uplink.UplinkMac.Should().BeEmpty();
    }

    [Fact]
    public void Fill_MeshChildClaimedInDownlinkTableIsSkipped()
    {
        var express = Parse(ExpressJson);
        var gateway = Parse(GatewayJson);
        gateway.DownlinkTable = [new DownlinkTableEntry { SerialNo = ExpressMac }];

        UniFiDiscovery.FillWiredParentFromLldp([express, gateway]);

        express.Uplink!.UplinkMac.Should().BeEmpty();
    }

    [Fact]
    public void Fill_DeviceWiredBelowTheExpressIsNotItsParent()
    {
        var express = Parse(ExpressJson);
        var gateway = Parse(GatewayJson);
        // The LLDP neighbor on the Express's uplink port is a switch that uplinks to the Express.
        gateway.Uplink!.UplinkMac = ExpressMac;

        UniFiDiscovery.FillWiredParentFromLldp([express, gateway]);

        express.Uplink!.UplinkMac.Should().BeEmpty();
    }

    [Fact]
    public void Fill_WifiGatewayWithoutModeIsNotFilledAndStaysGateway()
    {
        // An Express running as the gateway: no mode field, an adopted LLDP neighbor on its WAN port.
        var gateway = Parse(ExpressJson);
        gateway.DeviceModeOverride = null;
        var neighbor = Parse(GatewayJson);
        neighbor.Type = "usw";

        UniFiDiscovery.FillWiredParentFromLldp([gateway, neighbor]);

        gateway.Uplink!.UplinkMac.Should().BeEmpty();
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GatewayMac, ExpressMac };
        UniFiDiscovery.DetermineDeviceType(gateway, macs, NullLogger.Instance).Should().Be(DeviceType.Gateway);
    }

    #endregion

    #region SiteGateway

    private static DiscoveredDevice Device(string mac, DeviceType type, DeviceType hardware, string? mode = null) =>
        new() { Mac = mac, Name = mac, Type = type, HardwareType = hardware, DeviceModeOverride = mode };

    [Fact]
    public void SiteGateway_ExpressListedFirstReturnsRealGateway()
    {
        var express = Device(ExpressMac, DeviceType.AccessPoint, DeviceType.Gateway, "mesh");
        var gateway = Device(GatewayMac, DeviceType.Gateway, DeviceType.Gateway);

        UniFiDiscovery.SiteGateway([express, gateway]).Should().BeSameAs(gateway);
    }

    [Fact]
    public void SiteGateway_DemotedWifiGatewayAloneIsReturned()
    {
        var demoted = Device(GatewayMac, DeviceType.AccessPoint, DeviceType.Gateway);
        var ap = Device("aa:bb:cc:00:00:05", DeviceType.AccessPoint, DeviceType.AccessPoint);

        UniFiDiscovery.SiteGateway([ap, demoted]).Should().BeSameAs(demoted);
    }

    [Fact]
    public void SiteGateway_OnlyExpressInApModeReturnsNull()
    {
        var express = Device(ExpressMac, DeviceType.AccessPoint, DeviceType.Gateway, "mesh");

        UniFiDiscovery.SiteGateway([express]).Should().BeNull();
        UniFiDiscovery.SiteGateway(null).Should().BeNull();
    }

    [Fact]
    public void SiteGateway_SingleGatewaySiteUnchanged()
    {
        var ap = Device("aa:bb:cc:00:00:05", DeviceType.AccessPoint, DeviceType.AccessPoint);
        var gateway = Device(GatewayMac, DeviceType.Gateway, DeviceType.Gateway);

        UniFiDiscovery.SiteGateway([ap, gateway]).Should().BeSameAs(gateway);
    }

    [Fact]
    public void SiteGateways_HaPairReturnsBoth()
    {
        var a = Device(GatewayMac, DeviceType.Gateway, DeviceType.Gateway);
        var b = Device("aa:bb:cc:00:00:06", DeviceType.Gateway, DeviceType.Gateway);
        var express = Device(ExpressMac, DeviceType.AccessPoint, DeviceType.Gateway, "mesh");

        UniFiDiscovery.SiteGateways([a, express, b]).Should().Equal(a, b);
    }

    #endregion
}

using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The uplink block is parsed on every device fetch, so one field the model cannot read would
/// take the whole device list with it. These hold the shapes the console actually sends and the
/// stringified ones other UniFi fields have been seen to arrive as.
/// </summary>
public class UplinkInfoParsingTests
{
    private static UniFiDeviceResponse Parse(string json) =>
        JsonSerializer.Deserialize<UniFiDeviceResponse>(json)!;

    [Fact]
    public void WiredLastUplink_ReadsTheAttachment()
    {
        var device = Parse("""
            { "type": "usw", "last_uplink": { "port_idx": 9, "uplink_mac": "aa:bb:cc:dd:ee:01",
              "uplink_device_name": "Core Switch", "uplink_remote_port": 10, "type": "wire" } }
            """);

        device.LastUplink!.UplinkMac.Should().Be("aa:bb:cc:dd:ee:01");
        device.LastUplink.UplinkRemotePort.Should().Be(10);
        device.LastUplink.PortIdx.Should().Be(9);
    }

    [Fact]
    public void MeshLastUplink_IsJustAParentAndAType()
    {
        // A wireless (including MLO) child's last_uplink carries no port or rate fields at all.
        var device = Parse("""
            { "type": "uap", "last_uplink": { "uplink_mac": "aa:bb:cc:dd:ee:02", "type": "wireless" } }
            """);

        device.LastUplink!.UplinkMac.Should().Be("aa:bb:cc:dd:ee:02");
        device.LastUplink.Type.Should().Be("wireless");
        device.LastUplink.UplinkRemotePort.Should().Be(0);
        device.LastUplink.PortIdx.Should().BeNull();
    }

    [Fact]
    public void AGatewaysLiveUplinkNamesNothing_AndItsLastUplinkNamesTheSwitch()
    {
        var device = Parse("""
            { "type": "uxg", "uplink": { "type": "wire" },
              "last_uplink": { "port_idx": 7, "uplink_mac": "aa:bb:cc:dd:ee:03", "uplink_remote_port": 2, "type": "wire" } }
            """);

        device.Uplink!.UplinkMac.Should().BeEmpty();
        device.LastUplink!.UplinkMac.Should().Be("aa:bb:cc:dd:ee:03");
    }

    [Fact]
    public void StringifiedAndEmptyNumbers_DoNotFailTheDevice()
    {
        var device = Parse("""
            { "type": "uap", "uplink": { "uplink_mac": "aa:bb:cc:dd:ee:04", "type": "wireless",
              "uplink_remote_port": "3", "port_idx": "1", "speed": "", "channel": "36",
              "signal": "-61", "noise": null, "tx_rate": "866000", "rx_rate": 720.5 } }
            """);

        var uplink = device.Uplink!;
        uplink.UplinkRemotePort.Should().Be(3);
        uplink.PortIdx.Should().Be(1);
        uplink.Speed.Should().Be(0);
        uplink.Channel.Should().Be(36);
        uplink.Signal.Should().Be(-61);
        uplink.Noise.Should().BeNull();
        uplink.TxRate.Should().Be(866000);
        uplink.RxRate.Should().Be(720);
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class GatewayDiagnosticsServiceTests
{
    [Theory]
    [InlineData("ppp0", "eth6")]
    [InlineData("ppp1", "eth4")]
    [InlineData("ppp2", "eth0")]
    [InlineData("ppp9", null)]
    public void FindWanPhysicalInterface_MatchesTheRequestedUplink(string uplinkInterface, string? expected)
    {
        var devices = JsonSerializer.Deserialize<List<UniFiDeviceResponse>>("""
            [
              {
                "type": "udm",
                "device_mode_override": "managed_shadow_gateway",
                "wan1": { "uplink_ifname": "ppp0", "ifname": "eth9" }
              },
              {
                "type": "ucg",
                "wan1": { "uplink_ifname": "ppp0", "ifname": "eth6" },
                "wan2": { "uplink_ifname": "ppp1", "name": "eth4" },
                "wan3": { "uplink_ifname": "ppp2", "port_idx": 3, "name": "Internet" },
                "port_table": [{ "port_idx": 3, "ifname": "eth0" }]
              }
            ]
            """)!;

        UniFiConnectionService.FindWanPhysicalInterface(devices, uplinkInterface).Should().Be(expected);
    }

    [Theory]
    [InlineData("ppp0", "eth6", "eth6")]
    [InlineData("ppp1", "eth4.7", "eth4")]
    [InlineData("ppp0", null, "ppp0")]
    [InlineData("ppp0", "eth6; false", "ppp0")]
    [InlineData("ppp0", "eth6\n", "ppp0")]
    [InlineData("eth6.7", null, "eth6")]
    [InlineData("eth6", null, "eth6")]
    public async Task RunAsync_OnlyUsesThePhysicalPortForSfpReadout(
        string interfaceName, string? physicalInterfaceName, string sfpInterface)
    {
        var output = $"""
            ###ADDR
            2: {interfaceName}: <UP,LOWER_UP> mtu 1492 state UP
                inet 203.0.113.5/32 scope global
            ###ROUTE
            ###SFP
            Identifier : 0x03 (SFP)
            ###NEIGH
            """;
        var ssh = new Mock<IGatewaySshService>(MockBehavior.Strict);
        ssh.Setup(s => s.RunCommandAsync(
                It.Is<string>(command =>
                    command.Contains($"ip -d addr show dev {interfaceName} 2>&1;") &&
                    command.Contains($"ethtool -m {sfpInterface} 2>&1;") &&
                    command.Contains($"ip neigh show dev {interfaceName} 2>&1;")),
                TimeSpan.FromSeconds(20), CancellationToken.None))
            .ReturnsAsync((true, output));
        var service = new GatewayDiagnosticsService(
            ssh.Object, Mock.Of<IIeeeOuiDatabase>(), NullLogger<GatewayDiagnosticsService>.Instance);

        var result = await service.RunAsync(interfaceName, physicalInterfaceName: physicalInterfaceName);

        result.RunError.Should().BeNull();
        result.InterfaceInfo!.Name.Should().Be(interfaceName);
        result.SfpInterface.Should().Be(sfpInterface);
        result.SfpModule!.Get("Identifier").Should().Be("0x03 (SFP)");
        result.RawOutput.Should().ContainKey($"ethtool -m {sfpInterface}");
        ssh.VerifyAll();
    }
}

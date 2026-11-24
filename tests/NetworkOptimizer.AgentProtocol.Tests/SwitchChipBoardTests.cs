using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

/// <summary>
/// Chip port to ethN from each board's own UniFi OS board file. The switch sections below are copied from the
/// 6.0.17 firmware images (research: gateway-firmware/switch-port-maps.md); the order differs by board.
/// </summary>
public class SwitchChipBoardTests
{
    private const string UxgFiber = @"{""switches"":[{""id"":""switch0"",""driver"":""RTL8372"",""cpu-ports"":[{""cpu-port"":5}],""edge-ports"":[{""edge-port"":0,""interface"":{""id"":""eth4""}},{""edge-port"":1,""interface"":{""id"":""eth3""}},{""edge-port"":2,""interface"":{""id"":""eth2""}},{""edge-port"":3,""interface"":{""id"":""eth1""}},{""edge-port"":4,""interface"":{""id"":""eth0""}}]}]}";
    private const string Udr7 = @"{""switches"":[{""id"":""switch0"",""driver"":""RTL8372"",""cpu-ports"":[{""cpu-port"":5}],""edge-ports"":[{""edge-port"":0,""interface"":{""id"":""eth3""}},{""edge-port"":1,""interface"":{""id"":""eth2""}},{""edge-port"":2,""interface"":{""id"":""eth1""}},{""edge-port"":3,""interface"":{""id"":""eth0""}}]}]}";
    private const string Udr5g = @"{""switches"":[{""id"":""switch0"",""driver"":""RTL8372"",""cpu-ports"":[{""cpu-port"":5}],""edge-ports"":[{""edge-port"":0,""interface"":{""id"":""eth0""}},{""edge-port"":1,""interface"":{""id"":""eth1""}},{""edge-port"":2,""interface"":{""id"":""eth2""}},{""edge-port"":3,""interface"":{""id"":""eth3""}}]}]}";
    private const string UcgMax = @"{""switches"":[{""id"":""switch0"",""driver"":""AR83xx"",""cpu-ports"":[{""cpu-port"":0}],""edge-ports"":[{""edge-port"":4,""interface"":{""id"":""eth3""}},{""edge-port"":3,""interface"":{""id"":""eth2""}},{""edge-port"":2,""interface"":{""id"":""eth1""}},{""edge-port"":1,""interface"":{""id"":""eth0""}}]}]}";
    private const string UxgMax = @"{""switches"":[{""id"":""switch0"",""driver"":""AR83xx"",""cpu-ports"":[{""cpu-port"":0}],""edge-ports"":[{""edge-port"":4,""interface"":{""id"":""eth0""}},{""edge-port"":3,""interface"":{""id"":""eth1""}},{""edge-port"":2,""interface"":{""id"":""eth2""}},{""edge-port"":1,""interface"":{""id"":""eth3""}}]}]}";
    private const string Udm = @"{""switches"":[{""id"":""switch0"",""driver"":""AR83xx"",""cpu-ports"":[{""cpu-port"":0},{""cpu-port"":6}],""edge-ports"":[{""edge-port"":1,""interface"":{""id"":""eth0""}},{""edge-port"":2,""interface"":{""id"":""eth1""}},{""edge-port"":3,""interface"":{""id"":""eth2""}},{""edge-port"":4,""interface"":{""id"":""eth3""}},{""edge-port"":5,""interface"":{""id"":""eth4""}}]}]}";
    private const string UdmBeast = @"{""switches"":[{""id"":""0"",""driver"":""cpss"",""cpu-ports"":[{""cpu-port"":10}],""edge-ports"":[{""edge-port"":0,""interface"":{""id"":""eth2""}},{""edge-port"":1,""interface"":{""id"":""eth3""}},{""edge-port"":2,""interface"":{""id"":""eth4""}},{""edge-port"":3,""interface"":{""id"":""eth5""}},{""edge-port"":4,""interface"":{""id"":""eth6""}},{""edge-port"":5,""interface"":{""id"":""eth7""}},{""edge-port"":6,""interface"":{""id"":""eth8""}},{""edge-port"":7,""interface"":{""id"":""eth9""}},{""edge-port"":8,""interface"":{""id"":""eth10""}},{""edge-port"":9,""interface"":{""id"":""eth11""}}]}]}";
    private const string UxgLite = @"{""switches"":[]}";

    private static string Map(string json, int cpu, params int[] ports) =>
        SwitchChipBoard.PortMap(json, cpu, ports) is { } m ? string.Join(",", m.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")) : "none";

    [Fact] public void UxgFiber_IsReversed() => Map(UxgFiber, 5, 0, 1, 2, 3, 4).Should().Be("0:eth4,1:eth3,2:eth2,3:eth1,4:eth0");
    [Fact] public void Udr7_IsReversed() => Map(Udr7, 5, 0, 1, 2, 3, 4).Should().Be("0:eth3,1:eth2,2:eth1,3:eth0");
    [Fact] public void Udr5g_IsInOrder() => Map(Udr5g, 5, 0, 1, 2, 3, 4).Should().Be("0:eth0,1:eth1,2:eth2,3:eth3");
    [Fact] public void UcgMax_IsOffset() => Map(UcgMax, 0, 1, 2, 3, 4, 5).Should().Be("1:eth0,2:eth1,3:eth2,4:eth3");
    [Fact] public void UxgMax_RunsOppositeToTheUcgMax() => Map(UxgMax, 0, 1, 2, 3, 4, 5).Should().Be("1:eth3,2:eth2,3:eth1,4:eth0");

    // The UDM lists two CPU ports; swconfig reports one of them.
    [Fact] public void Udm_AcceptsEitherCpuPort() => Map(Udm, 6, 0, 1, 2, 3, 4, 5).Should().Be("1:eth0,2:eth1,3:eth2,4:eth3,5:eth4");

    [Fact] public void ACpuPortSwconfigDoesNotReport_GivesNoMap() => Map(UxgFiber, 6, 0, 1, 2, 3, 4).Should().Be("none");
    [Fact] public void ANonSwconfigSwitch_GivesNoMap() => Map(UdmBeast, 10, 0, 1, 2).Should().Be("none");
    [Fact] public void ABoardWithNoSwitchChip_GivesNoMap() => Map(UxgLite, 5, 0, 1).Should().Be("none");
    [Fact] public void OnlyPortsSwconfigLists_AreMapped() => Map(UxgFiber, 5, 0, 1).Should().Be("0:eth4,1:eth3");
    [Fact] public void ABrokenFile_GivesNoMap() => Map("{not json", 5, 0).Should().Be("none");

    [Fact]
    public void BoardFile_IsPickedBySysid()
    {
        var files = new[] { "/c/ucg-fiber-a6a8.json", "/c/udr7-a67a.json", "/c/uxg-fiber-a6aa.json" };
        SwitchChipBoard.BoardFile("board.vendorid=0x0777\nboard.sysid=0xa67a\nboard.name=UniFi Dream Router 7\n", files).Should().Be("/c/udr7-a67a.json");
        SwitchChipBoard.BoardFile("board.sysid=0xffff\n", files).Should().BeNull();
        SwitchChipBoard.BoardFile("board.name=x\n", files).Should().BeNull();
    }
}
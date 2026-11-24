using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

/// <summary><c>swconfig</c> output as a UXG-Fiber prints it (captured 2026-11-23).</summary>
public class SwitchChipOutputTests
{
    [Fact]
    public void Describe_ReadsPortsCpuAndChip()
    {
        var (ports, cpu, model) = SwitchChipOutput.Describe("switch0: 90000.mdio(realtek,rtl8372), ports: 6 (cpu @ 5), vlans: 4096\n     --switch\n\tAttribute 1 (int): debug (Debug mode)\n");
        ports.Should().Equal(0, 1, 2, 3, 4);
        cpu.Should().Be(5);
        model.Should().Be("realtek,rtl8372");
        SwitchChipOutput.Describe("No such device").Cpu.Should().Be(-1);
        SwitchChipOutput.Describe(null).Ports.Should().BeEmpty();
    }

    [Theory]
    [InlineData("port:1 link:up speed:1000baseT full-duplex txflow rxflow auto", true, 1000, true)]
    [InlineData("port:2 link:up speed:2500baseT half-duplex auto", true, 2500, false)]
    [InlineData("port:3 link:force-down ", false, 0, false)]
    [InlineData("port:0 link:down", false, 0, false)]
    public void ParseLink_ReadsStateSpeedAndDuplex(string line, bool up, int speed, bool full) =>
        SwitchChipOutput.ParseLink(line).Should().Be((up, speed, full));

    [Fact]
    public void ParseLink_UnreadableIsNull() => SwitchChipOutput.ParseLink("garbage").Should().BeNull();

    [Fact]
    public void ParseMib_ReadsCountersAndSkipsTheHeader()
    {
        var mib = SwitchChipOutput.ParseMib("Port 1 MIB counters\nifInOctets_H                            : 172637804137\ndot3InPauseFrames                       : 355\nrx_etherStatsCRCAlignErrors             : 0\nbogus line\n");
        mib.Should().HaveCount(3);
        mib["ifInOctets_H"].Should().Be(172_637_804_137);
        mib["dot3InPauseFrames"].Should().Be(355);
    }
}
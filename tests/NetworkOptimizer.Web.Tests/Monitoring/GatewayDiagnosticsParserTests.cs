using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Parser coverage for the read-only gateway interface diagnostics. The samples are the
/// shapes a UniFi gateway actually emits: iproute2 detail output with DHCP lifetimes, an
/// ethtool transceiver readout, and a neighbor table with resolved and failed entries.
/// </summary>
public class GatewayDiagnosticsParserTests
{
    private const string DhcpWanAddrOutput = """
        5: eth4: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default qlen 1000
            link/ether aa:bb:cc:dd:ee:ff brd ff:ff:ff:ff:ff:ff promiscuity 0 minmtu 68 maxmtu 9000
            inet 203.0.113.5/24 brd 203.0.113.255 scope global dynamic eth4
               valid_lft 421029sec preferred_lft 421029sec
            inet6 2001:db8::5/64 scope global dynamic
               valid_lft 2591992sec preferred_lft 604792sec
            inet6 fe80::1/64 scope link
               valid_lft forever preferred_lft forever
        """;

    [Fact]
    public void ParseAddressOutput_ReadsInterfaceHeader()
    {
        var info = GatewayDiagnosticsParser.ParseAddressOutput(DhcpWanAddrOutput, "eth4");

        info.Should().NotBeNull();
        info!.Name.Should().Be("eth4");
        info.State.Should().Be("UP");
        info.Mtu.Should().Be(1500);
        info.MacAddress.Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void ParseAddressOutput_ReadsDhcpLeaseOnIpv4Address()
    {
        var info = GatewayDiagnosticsParser.ParseAddressOutput(DhcpWanAddrOutput, "eth4");

        var v4 = info!.Addresses.Should().ContainSingle(a => !a.IsIpv6).Subject;
        v4.Cidr.Should().Be("203.0.113.5/24");
        v4.Address.Should().Be("203.0.113.5");
        v4.PrefixLength.Should().Be(24);
        v4.SubnetMask.Should().Be("255.255.255.0");
        v4.Broadcast.Should().Be("203.0.113.255");
        v4.Scope.Should().Be("global");
        v4.IsDynamic.Should().BeTrue();
        v4.ValidLifetimeSeconds.Should().Be(421029);
        v4.PreferredLifetimeSeconds.Should().Be(421029);
        v4.ValidLifetime.Should().Be(TimeSpan.FromSeconds(421029));
    }

    [Fact]
    public void ParseAddressOutput_PutsIpv4First()
    {
        var info = GatewayDiagnosticsParser.ParseAddressOutput(DhcpWanAddrOutput, "eth4");

        info!.Addresses.Should().HaveCount(3);
        info.Addresses[0].IsIpv6.Should().BeFalse();
    }

    [Fact]
    public void ParseAddressOutput_TreatsForeverAsNoExpiry()
    {
        var info = GatewayDiagnosticsParser.ParseAddressOutput(DhcpWanAddrOutput, "eth4");

        var linkLocal = info!.Addresses.Single(a => a.Scope == "link");
        linkLocal.ValidLifetimeSeconds.Should().BeNull();
        linkLocal.ValidLifetime.Should().BeNull();
    }

    [Fact]
    public void ParseAddressOutput_MarksStaticAddressNotDynamic()
    {
        const string output = """
            3: eth0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default
                link/ether aa:bb:cc:00:11:22 brd ff:ff:ff:ff:ff:ff
                inet 198.51.100.10/29 brd 198.51.100.15 scope global eth0
                   valid_lft forever preferred_lft forever
            """;

        var info = GatewayDiagnosticsParser.ParseAddressOutput(output, "eth0");

        var addr = info!.Addresses.Should().ContainSingle().Subject;
        addr.IsDynamic.Should().BeFalse();
        addr.SubnetMask.Should().Be("255.255.255.248");
        addr.ValidLifetime.Should().BeNull();
    }

    [Fact]
    public void ParseAddressOutput_HandlesAddressWithNoLifetimeLine()
    {
        const string output = """
            5: eth4: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP
                link/ether aa:bb:cc:dd:ee:ff brd ff:ff:ff:ff:ff:ff
                inet 203.0.113.5/24 brd 203.0.113.255 scope global eth4
            """;

        var info = GatewayDiagnosticsParser.ParseAddressOutput(output, "eth4");

        var addr = info!.Addresses.Should().ContainSingle().Subject;
        addr.Address.Should().Be("203.0.113.5");
        addr.ValidLifetimeSeconds.Should().BeNull();
    }

    [Fact]
    public void ParseAddressOutput_ReadsVlanTaggedInterfaceName()
    {
        const string output = """
            9: eth4.201@eth4: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc noqueue state UP
                link/ether aa:bb:cc:dd:ee:ff brd ff:ff:ff:ff:ff:ff
                inet 203.0.113.9/30 scope global dynamic eth4.201
                   valid_lft 3600sec preferred_lft 3600sec
            """;

        var info = GatewayDiagnosticsParser.ParseAddressOutput(output, "fallback");

        info!.Name.Should().Be("eth4.201");
        info.Addresses.Single().ValidLifetimeSeconds.Should().Be(3600);
    }

    [Fact]
    public void ParseAddressOutput_ReturnsNullForErrorText()
    {
        var info = GatewayDiagnosticsParser.ParseAddressOutput(
            "Device \"eth99\" does not exist.", "eth99");

        info.Should().BeNull();
    }

    [Theory]
    [InlineData(32, "255.255.255.255")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(22, "255.255.252.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(0, "0.0.0.0")]
    public void PrefixToMask_ConvertsPrefixLength(int prefix, string expected)
    {
        GatewayDiagnosticsParser.PrefixToMask(prefix).Should().Be(expected);
    }

    [Fact]
    public void PrefixToMask_RejectsOutOfRangePrefix()
    {
        GatewayDiagnosticsParser.PrefixToMask(33).Should().BeNull();
        GatewayDiagnosticsParser.PrefixToMask(-1).Should().BeNull();
    }

    [Fact]
    public void ParseEthtoolModuleOutput_ReadsFieldsInOrder()
    {
        const string output = """
            	Identifier                                : 0x03 (SFP)
            	Connector                                 : 0x07 (LC)
            	Vendor name                               : TestOptics
            	Vendor PN                                 : TO-BX-U
            	Vendor SN                                 : SN12345678
            	Module temperature                        : 45.12 degrees C / 113.21 degrees F
            	Module voltage                            : 3.3000 V
            	Laser output power                        : 1.9800 mW / 2.97 dBm
            	Receiver signal average optical power     : 0.1234 mW / -9.09 dBm
            """;

        var module = GatewayDiagnosticsParser.ParseEthtoolModuleOutput(output);

        module.Should().NotBeNull();
        module!.Fields.Should().HaveCount(9);
        module.Fields[0].Name.Should().Be("Identifier");
        module.Fields[0].Value.Should().Be("0x03 (SFP)");
        module.Get("Vendor name").Should().Be("TestOptics");
        module.Get("Module voltage").Should().Be("3.3000 V");
    }

    [Fact]
    public void ParseEthtoolModuleOutput_HighlightsUseFriendlyNames()
    {
        const string output = """
            	Identifier                                : 0x03 (SFP)
            	Vendor name                               : TestOptics
            	Vendor PN                                 : TO-BX-U
            	Laser output power                        : 1.9800 mW / 2.97 dBm
            	Receiver signal average optical power     : 0.1234 mW / -9.09 dBm
            """;

        var highlights = GatewayDiagnosticsParser.ParseEthtoolModuleOutput(output)!
            .Highlights().ToList();

        highlights.Select(h => h.Name).Should().ContainInOrder(
            "Vendor", "Part number", "Identifier", "TX power", "RX power");
        highlights.Should().NotContain(h => h.Name == "Serial number");
    }

    [Fact]
    public void ParseEthtoolModuleOutput_SkipsEepromHexDumpLines()
    {
        const string output = """
            	Identifier                                : 0x03 (SFP)
            	0x0000: 03 04 07 00 00 00 00 00
            	0x0010: 00 00 00 67 00 00 00 00
            """;

        var module = GatewayDiagnosticsParser.ParseEthtoolModuleOutput(output);

        module!.Fields.Should().ContainSingle();
        module.Fields[0].Name.Should().Be("Identifier");
    }

    [Fact]
    public void ParseEthtoolModuleOutput_ReturnsNullWhenModuleUnsupported()
    {
        var module = GatewayDiagnosticsParser.ParseEthtoolModuleOutput(
            "Cannot get module EEPROM information: Operation not supported");

        module.Should().BeNull();
    }

    [Fact]
    public void ParseNeighborOutput_ReadsResolvedEntries()
    {
        const string output = """
            203.0.113.1 lladdr 00:11:22:33:44:55 router REACHABLE
            203.0.113.7 lladdr 00:11:22:33:44:66 STALE
            fe80::1 lladdr 00:11:22:33:44:55 router STALE
            """;

        var neighbors = GatewayDiagnosticsParser.ParseNeighborOutput(output);

        neighbors.Should().HaveCount(3);
        var gateway = neighbors.Single(n => n.IpAddress == "203.0.113.1");
        gateway.MacAddress.Should().Be("00:11:22:33:44:55");
        gateway.State.Should().Be("REACHABLE");
        gateway.IsRouter.Should().BeTrue();
        gateway.IsIpv6.Should().BeFalse();

        neighbors.Single(n => n.IpAddress == "fe80::1").IsIpv6.Should().BeTrue();
    }

    [Fact]
    public void ParseNeighborOutput_KeepsUnresolvedEntriesButSortsThemLast()
    {
        const string output = """
            203.0.113.1 FAILED
            203.0.113.7 lladdr 00:11:22:33:44:66 REACHABLE
            """;

        var neighbors = GatewayDiagnosticsParser.ParseNeighborOutput(output);

        neighbors.Should().HaveCount(2);
        neighbors[0].IpAddress.Should().Be("203.0.113.7");
        neighbors[1].MacAddress.Should().BeNull();
        neighbors[1].State.Should().Be("FAILED");
    }

    [Fact]
    public void ParseNeighborOutput_HandlesDevColumnFromUnscopedQuery()
    {
        const string output = "203.0.113.1 dev eth4 lladdr 00:11:22:33:44:55 REACHABLE";

        var neighbor = GatewayDiagnosticsParser.ParseNeighborOutput(output).Should().ContainSingle().Subject;

        neighbor.IpAddress.Should().Be("203.0.113.1");
        neighbor.MacAddress.Should().Be("00:11:22:33:44:55");
        neighbor.State.Should().Be("REACHABLE");
    }

    [Fact]
    public void ParseNeighborOutput_IgnoresNonAddressLines()
    {
        var neighbors = GatewayDiagnosticsParser.ParseNeighborOutput(
            "Cannot find device \"eth99\"");

        neighbors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("eth4")]
    [InlineData("eth4.201")]
    [InlineData("br0")]
    [InlineData("ppp0")]
    [InlineData("wan-1")]
    public void IsValidInterfaceName_AcceptsRealInterfaceNames(string name)
    {
        GatewayDiagnosticsParser.IsValidInterfaceName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("eth4; reboot")]
    [InlineData("eth4 && rm -rf /")]
    [InlineData("$(id)")]
    [InlineData("`id`")]
    [InlineData("eth4|cat")]
    [InlineData("../etc/passwd")]
    public void IsValidInterfaceName_RejectsAnythingShellCanAbuse(string? name)
    {
        GatewayDiagnosticsParser.IsValidInterfaceName(name).Should().BeFalse();
    }
}

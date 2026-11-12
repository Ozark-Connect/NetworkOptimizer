using FluentAssertions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class FirewallSubversionDescriberTests
{
    private static FirewallRule Rule(
        string action,
        string protocol = "all",
        string? destPort = null,
        string? sourcePort = null,
        string ipVersion = "BOTH",
        string srcTarget = "ANY",
        List<string>? srcNets = null,
        List<string>? srcIps = null,
        List<string>? srcMacs = null,
        bool srcNetsOpposite = false,
        string dstTarget = "ANY",
        List<string>? dstNets = null,
        List<string>? dstIps = null,
        List<string>? domains = null,
        List<int>? apps = null,
        bool dstPortsOpposite = false,
        bool unresolvedPortGroup = false,
        bool oppositeProtocol = false,
        string? icmpType = null,
        string? schedule = null,
        string? stateType = null,
        List<string>? states = null,
        string? srcZone = "internal-zone",
        string? dstZone = "external-zone") => new()
    {
        Id = action + Guid.NewGuid(),
        Name = action,
        Enabled = true,
        Action = action,
        Protocol = protocol,
        MatchOppositeProtocol = oppositeProtocol,
        IpVersion = ipVersion,
        DestinationPort = destPort,
        DestinationMatchOppositePorts = dstPortsOpposite,
        HasUnresolvedDestinationPortGroup = unresolvedPortGroup,
        SourcePort = sourcePort,
        SourceMatchingTarget = srcTarget,
        SourceNetworkIds = srcNets,
        SourceMatchOppositeNetworks = srcNetsOpposite,
        SourceIps = srcIps,
        SourceClientMacs = srcMacs,
        DestinationMatchingTarget = dstTarget,
        DestinationNetworkIds = dstNets,
        DestinationIps = dstIps,
        WebDomains = domains,
        AppIds = apps,
        IcmpTypename = icmpType,
        ScheduleMode = schedule,
        ConnectionStateType = stateType,
        ConnectionStates = states,
        SourceZoneId = srcZone,
        DestinationZoneId = dstZone
    };

    private static readonly List<NetworkInfo> Networks =
    [
        new() { Id = "net-home", Name = "Home", VlanId = 1, Subnet = "192.168.1.0/24" },
        new() { Id = "net-iot", Name = "IoT", VlanId = 20, Subnet = "192.168.20.0/24" }
    ];

    private static void ShouldBeTotal(FirewallRule allow, FirewallRule deny) =>
        FirewallSubversionDescriber.Describe(allow, deny, Networks).Should().Be(new SubversionDescription(SubversionExtent.Total, null));

    private static void ShouldBePartial(FirewallRule allow, FirewallRule deny, string slice)
    {
        var result = FirewallSubversionDescriber.Describe(allow, deny, Networks);
        result.Extent.Should().Be(SubversionExtent.Partial);
        result.Slice.Should().Be(slice);
    }

    private static bool FewPorts(FirewallRule allow, FirewallRule deny) =>
        FirewallSubversionDescriber.Describe(allow, deny, Networks).FewPorts;

    // --- Few ports (scores 0) ---

    [Theory]
    [InlineData("53", true)]
    [InlineData("53,853", true)]
    [InlineData("80,443,8443", true)]
    [InlineData("80,443,8080,8443", false)]
    [InlineData("8000-8080", false)]
    public void FewPorts_IsAtMostThreeDestinationPorts(string ports, bool expected) =>
        FewPorts(Rule("ALLOW", "tcp_udp", ports), Rule("BLOCK")).Should().Be(expected);

    [Fact]
    public void FewPorts_CountsOnlyThePortsTakenFromTheDeny() =>
        FewPorts(Rule("ALLOW", "tcp", "1-1000"), Rule("BLOCK", "tcp", "22,80,443,5000")).Should().BeTrue();

    [Fact]
    public void FewPorts_IsFalseWithoutAPortNarrowing()
    {
        FewPorts(Rule("ALLOW", srcTarget: "CLIENT", srcMacs: ["aa:bb:cc:dd:ee:01"]), Rule("BLOCK")).Should().BeFalse();
        FewPorts(Rule("ALLOW", "tcp"), Rule("BLOCK")).Should().BeFalse();
        FewPorts(Rule("ALLOW", "udp", sourcePort: "5353", destPort: "1-65535"), Rule("BLOCK")).Should().BeFalse();
    }

    [Fact]
    public void FewPorts_IsFalseWhenTotal() =>
        FewPorts(Rule("ALLOW", "tcp", "53"), Rule("BLOCK", "tcp", "53")).Should().BeFalse();

    private static void ShouldBeUnknown(FirewallRule allow, FirewallRule deny) =>
        FirewallSubversionDescriber.Describe(allow, deny, Networks).Extent.Should().Be(SubversionExtent.Unknown);

    // --- Total ---

    [Fact]
    public void IdenticalScope_IsTotal() => ShouldBeTotal(Rule("ALLOW"), Rule("BLOCK"));

    [Fact]
    public void AllowWiderOnEveryDimension_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW"), Rule("BLOCK", "tcp", "443", srcTarget: "NETWORK", srcNets: ["net-home"], dstTarget: "IP", dstIps: ["203.0.113.5"]));

    // --- Protocol ---

    [Fact]
    public void TcpUdpPort53AllowBeforeAnyDeny_NamesProtocolAndPort() =>
        ShouldBePartial(Rule("ALLOW", "tcp_udp", "53"), Rule("BLOCK"), "TCP/UDP port 53 traffic");

    [Fact]
    public void TcpAllowBeforeTcpUdpDeny_NamesTcp() =>
        ShouldBePartial(Rule("ALLOW", "tcp"), Rule("BLOCK", "tcp_udp"), "TCP traffic");

    [Fact]
    public void TcpUdpAllowBeforeTcpDeny_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", "tcp_udp"), Rule("BLOCK", "tcp"));

    [Fact]
    public void InvertedProtocolAllow_CoversADenyOnAnotherProtocol() =>
        ShouldBeTotal(Rule("ALLOW", "icmp", oppositeProtocol: true), Rule("BLOCK", "tcp"));

    [Fact]
    public void InvertedProtocolAllow_BeforeAllProtocolDeny_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", "icmp", oppositeProtocol: true), Rule("BLOCK"));

    [Fact]
    public void SpecificIcmpTypeAllow_NamesTheType() =>
        ShouldBePartial(Rule("ALLOW", "icmp", icmpType: "echo-request"), Rule("BLOCK"), "ICMP echo-request traffic");

    [Fact]
    public void SpecificIcmpTypeAllow_BeforeSameTypeDeny_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", "icmp", icmpType: "echo-request"), Rule("BLOCK", "icmp", icmpType: "echo-request"));

    // --- Ports ---

    [Fact]
    public void AllowPortsSupersetOfDenyPorts_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", "tcp", "53,853"), Rule("BLOCK", "tcp", "53"));

    [Fact]
    public void AllowPortsPartlyOverlapDenyPorts_NamesOnlyTheSharedPorts() =>
        ShouldBePartial(Rule("ALLOW", "tcp", "53,853"), Rule("BLOCK", "tcp", "53,80"), "port 53 traffic");

    [Fact]
    public void SeveralPorts_AreListedAndRangesCompressed() =>
        ShouldBePartial(Rule("ALLOW", "tcp", "443,8000-8080,53"), Rule("BLOCK"), "TCP ports 53, 443, 8000-8080 traffic");

    [Fact]
    public void InvertedAllowPorts_BeforeAnyPortDeny_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", "tcp", "22", dstPortsOpposite: true), Rule("BLOCK", "tcp"));

    [Fact]
    public void InvertedAllowPorts_CoverADenyOnADifferentPort() =>
        ShouldBeTotal(Rule("ALLOW", "tcp", "22", dstPortsOpposite: true), Rule("BLOCK", "tcp", "80"));

    [Fact]
    public void UnresolvedAllowPortGroup_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", "tcp", unresolvedPortGroup: true), Rule("BLOCK"));

    [Fact]
    public void SourcePortWithDestinationPort_NamesBoth() =>
        ShouldBePartial(Rule("ALLOW", "udp", "5353", sourcePort: "5353"), Rule("BLOCK"), "UDP port 5353 traffic with source port 5353");

    [Fact]
    public void PortsOnIcmpAllow_AreIgnored() =>
        ShouldBePartial(Rule("ALLOW", "icmp", "53"), Rule("BLOCK"), "ICMP traffic");

    // --- IP version ---

    [Theory]
    [InlineData("IPV6", "IPv6")]
    [InlineData("IPV4", "IPv4")]
    public void SingleFamilyAllowBeforeDualStackDeny_NamesTheFamily(string allowVersion, string expected) =>
        ShouldBePartial(Rule("ALLOW", ipVersion: allowVersion), Rule("BLOCK"), $"{expected} traffic");

    [Fact]
    public void SameFamilyOnBoth_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", ipVersion: "IPV4"), Rule("BLOCK", ipVersion: "IPV4"));

    // --- Source ---

    [Fact]
    public void ClientAllowBeforeNetworkDeny_CountsClients() =>
        ShouldBePartial(Rule("ALLOW", "tcp_udp", "53", srcTarget: "CLIENT", srcMacs: ["aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02"]),
            Rule("BLOCK", srcTarget: "NETWORK", srcNets: ["net-home"]), "TCP/UDP port 53 traffic from 2 clients");

    [Fact]
    public void AllowClientsSupersetOfDenyClients_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", srcTarget: "CLIENT", srcMacs: ["aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02"]),
            Rule("BLOCK", srcTarget: "CLIENT", srcMacs: ["AA:BB:CC:DD:EE:01"]));

    [Fact]
    public void AllowClientsSubsetOfDenyClients_CountsTheSharedOnes() =>
        ShouldBePartial(Rule("ALLOW", srcTarget: "CLIENT", srcMacs: ["aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:09"]),
            Rule("BLOCK", srcTarget: "CLIENT", srcMacs: ["aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02"]), "traffic from 1 client");

    [Fact]
    public void AllowNetworkSubsetOfDenyNetworks_NamesIt() =>
        ShouldBePartial(Rule("ALLOW", srcTarget: "NETWORK", srcNets: ["net-home"]),
            Rule("BLOCK", srcTarget: "NETWORK", srcNets: ["net-home", "net-iot"]), "traffic from Home");

    [Fact]
    public void AllowNetworksSupersetOfDenyNetworks_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", srcTarget: "NETWORK", srcNets: ["net-home", "net-iot"]),
            Rule("BLOCK", srcTarget: "NETWORK", srcNets: ["net-iot"]));

    [Fact]
    public void UnnamedNetwork_IsCounted() =>
        ShouldBePartial(Rule("ALLOW", srcTarget: "NETWORK", srcNets: ["net-unknown"]), Rule("BLOCK"), "traffic from 1 network");

    [Fact]
    public void AllowNetworkContainingDenyIps_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", srcTarget: "NETWORK", srcNets: ["net-home"]),
            Rule("BLOCK", srcTarget: "IP", srcIps: ["192.168.1.50"]));

    [Fact]
    public void InvertedAllowNetworks_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", srcTarget: "NETWORK", srcNets: ["net-iot"], srcNetsOpposite: true), Rule("BLOCK"));

    // --- Destination ---

    [Fact]
    public void AllowIpBeforeAnyDeny_NamesTheIp() =>
        ShouldBePartial(Rule("ALLOW", dstTarget: "IP", dstIps: ["203.0.113.5"]), Rule("BLOCK"), "traffic to 203.0.113.5");

    [Fact]
    public void AllowCidrContainingDenyIp_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", dstTarget: "IP", dstIps: ["203.0.113.0/24"]), Rule("BLOCK", dstTarget: "IP", dstIps: ["203.0.113.5"]));

    [Fact]
    public void AllowIpInsideDenyCidr_NamesTheIp() =>
        ShouldBePartial(Rule("ALLOW", dstTarget: "IP", dstIps: ["203.0.113.5"]), Rule("BLOCK", dstTarget: "IP", dstIps: ["203.0.113.0/24"]), "traffic to 203.0.113.5");

    [Fact]
    public void ManyIps_AreCounted() =>
        ShouldBePartial(Rule("ALLOW", dstTarget: "IP", dstIps: ["203.0.113.1", "203.0.113.2", "203.0.113.3", "203.0.113.4"]),
            Rule("BLOCK"), "traffic to 4 addresses");

    [Fact]
    public void ParentDomainAllow_CoversSubdomainDeny() =>
        ShouldBeTotal(Rule("ALLOW", dstTarget: "WEB", domains: ["example.com"]), Rule("BLOCK", dstTarget: "WEB", domains: ["tracker.example.com"]));

    [Fact]
    public void SubdomainAllow_BeforeParentDomainDeny_NamesTheSubdomain() =>
        ShouldBePartial(Rule("ALLOW", dstTarget: "WEB", domains: ["tracker.example.com"]), Rule("BLOCK", dstTarget: "WEB", domains: ["example.com"]),
            "traffic to tracker.example.com");

    [Fact]
    public void DomainAllowBeforeAnyDeny_ListsUpToThreeDomains()
    {
        ShouldBePartial(Rule("ALLOW", dstTarget: "WEB", domains: ["a.example", "b.example"]), Rule("BLOCK"), "traffic to a.example, b.example");
        ShouldBePartial(Rule("ALLOW", dstTarget: "WEB", domains: ["a.example", "b.example", "c.example", "d.example"]), Rule("BLOCK"), "traffic to 4 domains");
    }

    [Fact]
    public void AppAllowBeforeAnyDeny_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", apps: [100]), Rule("BLOCK"));

    [Fact]
    public void AppAllowCoveringTheDenysApps_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW", apps: [100, 200]), Rule("BLOCK", apps: [100]));

    [Fact]
    public void AnyDestinationAllow_CoversAnAppDeny() =>
        ShouldBeTotal(Rule("ALLOW"), Rule("BLOCK", apps: [100]));

    [Fact]
    public void RegionAllow_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", dstTarget: "REGION"), Rule("BLOCK"));

    // --- Zones, states, schedule ---

    [Fact]
    public void AllowPinnedToAZoneTheDenyDoesNotPin_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW"), Rule("BLOCK", dstZone: null));

    [Fact]
    public void AllowOnFewerConnectionStates_IsUnknown() =>
        ShouldBeUnknown(Rule("ALLOW", stateType: "CUSTOM", states: ["NEW"]), Rule("BLOCK"));

    [Fact]
    public void ScheduledAllow_SaysItOnlyAppliesDuringTheSchedule() =>
        ShouldBePartial(Rule("ALLOW", schedule: "EVERY_DAY"), Rule("BLOCK"), "traffic, during the allow rule's schedule");

    [Fact]
    public void ScheduledDeny_UnderAlwaysOnAllow_IsTotal() =>
        ShouldBeTotal(Rule("ALLOW"), Rule("BLOCK", schedule: "EVERY_DAY"));

    // --- Several dimensions at once ---

    [Fact]
    public void EveryNarrowedDimension_IsNamedInOrder() =>
        ShouldBePartial(
            Rule("ALLOW", "tcp", "443", ipVersion: "IPV4", srcTarget: "NETWORK", srcNets: ["net-home"], dstTarget: "IP", dstIps: ["203.0.113.5"], schedule: "EVERY_DAY"),
            Rule("BLOCK"),
            "IPv4 TCP port 443 traffic from Home to 203.0.113.5, during the allow rule's schedule");
}

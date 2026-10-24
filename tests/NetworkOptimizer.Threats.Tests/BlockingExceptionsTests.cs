using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class BlockingExceptionsTests
{
    private static BlockingException Outbound(string? device, string? address, ExceptionProtocol protocol = ExceptionProtocol.Tcp, int? port = 443) =>
        new() { Direction = ExceptionDirection.Outbound, Device = device, Address = address, Protocol = protocol, PortFrom = port, PortTo = port };

    [Fact]
    public void Validate_CanonicalizesAddresses()
    {
        var e = BlockingExceptions.Validate(Outbound(" 192.168.1.50/32 ", "203.0.113.9/24") with { Note = "  cloud  " });

        e.Device.Should().Be("192.168.1.50");
        e.Address.Should().Be("203.0.113.0/24");
        e.Note.Should().Be("cloud");
    }

    [Fact]
    public void Validate_RefusesAnExceptionForEverything()
    {
        var act = () => BlockingExceptions.Validate(Outbound(null, null, ExceptionProtocol.Any, null));
        act.Should().Throw<ArgumentException>().WithMessage("*turn the lists off*");
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.0/8")]
    [InlineData("203.0.0.0/7")]
    [InlineData("not an ip")]
    public void Validate_RefusesAListedAddressNoListCouldHold(string address)
    {
        var act = () => BlockingExceptions.Validate(Outbound(null, address));
        act.Should().Throw<ArgumentException>().WithMessage("Listed address*");
    }

    [Fact]
    public void Validate_RefusesMixedFamilies_AndAPortWithoutAProtocol()
    {
        ((Action)(() => BlockingExceptions.Validate(Outbound("2001:db8::5", "203.0.113.7")))).Should().Throw<ArgumentException>().WithMessage("*both be IPv4*");
        ((Action)(() => BlockingExceptions.Validate(Outbound(null, null, ExceptionProtocol.Any, 443)))).Should().Throw<ArgumentException>().WithMessage("Choose TCP*");
        ((Action)(() => BlockingExceptions.Validate(Outbound(null, null, ExceptionProtocol.Tcp, 0)))).Should().Throw<ArgumentException>().WithMessage("Port must*");
    }

    [Theory]
    [InlineData("443", 443, 443)]
    [InlineData(" 3478 - 3481 ", 3478, 3481)]
    public void ParsePort_ReadsAPortOrARange(string text, int from, int to) =>
        BlockingExceptions.ParsePort(text).Should().Be((from, to));

    [Fact]
    public void ParsePort_EmptyIsEveryPort_AndAnythingElseIsRefused()
    {
        BlockingExceptions.ParsePort("  ").Should().BeNull();
        ((Action)(() => BlockingExceptions.ParsePort("https"))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Matches_ChecksDirectionDeviceRangeProtocolAndPort()
    {
        var e = BlockingExceptions.Validate(Outbound("192.168.1.0/24", "203.0.113.7", ExceptionProtocol.TcpUdp, 3478) with { PortTo = 3481 });

        e.Matches(ExceptionDirection.Outbound, "192.168.1.50", "203.0.113.7", "UDP", 3480).Should().BeTrue();
        e.Matches(ExceptionDirection.Inbound, "192.168.1.50", "203.0.113.7", "udp", 3480).Should().BeFalse();
        e.Matches(ExceptionDirection.Outbound, "192.168.2.50", "203.0.113.7", "udp", 3480).Should().BeFalse();
        e.Matches(ExceptionDirection.Outbound, "192.168.1.50", "203.0.113.8", "udp", 3480).Should().BeFalse();
        e.Matches(ExceptionDirection.Outbound, "192.168.1.50", "203.0.113.7", "icmp", null).Should().BeFalse();
        e.Matches(ExceptionDirection.Outbound, "192.168.1.50", "203.0.113.7", "tcp", 3482).Should().BeFalse();
    }

    [Fact]
    public void Describe_SaysWhatGetsThrough()
    {
        BlockingExceptions.Describe(Outbound("192.168.1.50", "203.0.113.7"), ip => ip == "192.168.1.50" ? "Thermostat" : null)
            .Should().Be("192.168.1.50 (Thermostat) can reach 203.0.113.7 on TCP 443.");
        BlockingExceptions.Describe(new BlockingException { Direction = ExceptionDirection.Inbound, Protocol = ExceptionProtocol.Udp, PortFrom = 51820, PortTo = 51820 })
            .Should().Be("Every listed address can reach every device and the gateway on UDP 51820.");
    }

    [Fact]
    public void Fingerprint_ChangesWithAnyEnforcedField()
    {
        var a = BlockingExceptions.Validate(Outbound("192.168.1.50", "203.0.113.7"));
        BlockingExceptions.Fingerprint([a]).Should().NotBe(BlockingExceptions.Fingerprint([a with { PortFrom = 8443, PortTo = 8443 }]));
        BlockingExceptions.Fingerprint([a]).Should().Be(BlockingExceptions.Fingerprint([a with { Note = "changed" }]), "the note is not enforced");
    }

    private static readonly GatewayBlocklistState State = new()
    {
        GroupV4Id = "own4", GroupV6Id = "own6", FeedsV4Id = "feeds4", FeedsV6Id = "feeds6", FeedsOutV4Id = "out4", FeedsOutV6Id = "out6",
    };

    private static readonly ExceptionZone[] Zones = [new("lan", false), new("gw", true)];

    [Fact]
    public void ConsolePolicies_Outbound_AllowPerInternalZone_PlusItsReplies()
    {
        var e = BlockingExceptions.Validate(Outbound("192.168.1.50", null));

        var specs = GatewayExceptionPolicies.Build([e], State, "ext", Zones, outbound: true);

        specs.Select(s => (s.SourceZoneId, s.DestinationZoneId)).Should().Equal(("lan", "ext"), ("ext", "lan"));
        var allow = specs[0].Body;
        allow.Action.Should().Be("ALLOW");
        allow.IpVersion.Should().Be("IPV4");
        allow.Protocol.Should().Be("tcp");
        allow.ConnectionStateType.Should().Be("ALL");
        var destination = (Dictionary<string, object>)allow.Destination;
        destination["ip_group_id"].Should().Be("out4", "any listed address means the outbound Network List, never any address");
        destination["port"].Should().Be("443");
        var reply = specs[1].Body;
        reply.ConnectionStates.Should().Equal("ESTABLISHED", "RELATED");
        ((Dictionary<string, object>)reply.Source)["ip_group_id"].Should().Be("feeds4");
        ((Dictionary<string, object>)reply.Source)["port"].Should().Be("443");
    }

    [Fact]
    public void ConsolePolicies_WithoutOutboundBlocking_OnlyTheRepliesAreNeeded()
    {
        var specs = GatewayExceptionPolicies.Build([BlockingExceptions.Validate(Outbound("192.168.1.50", "203.0.113.7"))], State, "ext", Zones, outbound: false);

        specs.Should().ContainSingle().Which.SourceZoneId.Should().Be("ext");
    }

    [Fact]
    public void ConsolePolicies_Inbound_ReachEveryZone_IncludingTheGateway()
    {
        var e = BlockingExceptions.Validate(new BlockingException
        {
            Direction = ExceptionDirection.Inbound, Protocol = ExceptionProtocol.Udp, PortFrom = 51820, PortTo = 51820,
        });

        var specs = GatewayExceptionPolicies.Build([e], State, "ext", Zones, outbound: true);

        // Both families, both zones in; replies only from the LAN zone (the Gateway zone is never blocked outbound).
        specs.Where(s => s.SourceZoneId == "ext").Select(s => s.DestinationZoneId).Should().BeEquivalentTo(["lan", "gw", "lan", "gw"]);
        specs.Where(s => s.DestinationZoneId == "ext").Select(s => s.SourceZoneId).Should().Equal("lan", "lan");
        specs.Select(s => s.Body.IpVersion).Distinct().Should().BeEquivalentTo(["IPV4", "IPV6"]);
    }

    [Fact]
    public void ConsolePolicies_NeverWidenToEveryAddress_WhenTheListGroupIsMissing()
    {
        var bare = new GatewayBlocklistState { GroupV4Id = "own4", GroupV6Id = "own6" };
        var e = BlockingExceptions.Validate(new BlockingException
        {
            Direction = ExceptionDirection.Inbound, Protocol = ExceptionProtocol.Tcp, PortFrom = 443, PortTo = 443,
        });

        GatewayExceptionPolicies.Build([e], bare, "ext", Zones, outbound: true).Should().BeEmpty();
    }

    [Fact]
    public void ConsolePolicies_KeyNamesThePolicy_HashFollowsItsContent()
    {
        var e = BlockingExceptions.Validate(Outbound("192.168.1.50", "203.0.113.7"));
        var specs = GatewayExceptionPolicies.Build([e], State, "ext", Zones, true);
        var edited = GatewayExceptionPolicies.Build([e with { PortFrom = 80, PortTo = 80 }], State, "ext", Zones, true);

        // An edit keeps every key, so each policy is updated in place; only its content hash moves.
        edited.Select(s => s.Key).Should().Equal(specs.Select(s => s.Key));
        edited.Select(s => s.Hash).Should().NotIntersectWith(specs.Select(s => s.Hash));
        GatewayExceptionPolicies.Build([e with { Note = "x" }], State, "ext", Zones, true).Select(s => s.Hash)
            .Should().Equal(specs.Select(s => s.Hash), "the note is not on the console");
    }

    private static readonly ExceptionZone[] SiteZones =
    [
        new("lan", false, ["192.168.1.1/24", "2001:db8:1::1/64"]),
        new("iot", false, ["192.168.20.1/24"]),
        new("vpn", false, ["192.168.7.1/24"]),
        new("gw", true),
    ];

    [Theory]
    [InlineData("192.168.1.50", "lan")]
    [InlineData("192.168.20.0/28", "iot")]
    [InlineData("192.168.7.2", "vpn")]
    [InlineData("2001:db8:1::50", "lan")]
    public void ZonesOf_PlacesADeviceInTheZoneOfItsNetwork(string device, string zone) =>
        GatewayExceptionPolicies.ZonesOf(device, SiteZones).Select(z => z.Id).Should().Equal(zone);

    [Fact]
    public void ZonesOf_ARangeAcrossNetworks_GetsEachZone() =>
        GatewayExceptionPolicies.ZonesOf("192.168.0.0/16", SiteZones).Select(z => z.Id).Should().Equal("lan", "iot", "vpn");

    [Theory]
    [InlineData(null)]
    [InlineData("10.99.0.5")]
    [InlineData("2001:db8:ff::5")]
    public void ZonesOf_NoDeviceOrNoNetworkHoldingIt_IsEveryZone(string? device) =>
        GatewayExceptionPolicies.ZonesOf(device, SiteZones).Should().BeSameAs(SiteZones);

    [Fact]
    public void ConsolePolicies_Outbound_OnlyInTheDevicesZone()
    {
        var specs = GatewayExceptionPolicies.Build([BlockingExceptions.Validate(Outbound("192.168.7.2", null))], State, "ext", SiteZones, true);

        specs.Select(s => (s.SourceZoneId, s.DestinationZoneId)).Should().Equal(("vpn", "ext"), ("ext", "vpn"));
    }

    [Fact]
    public void ConsolePolicies_Inbound_ToADevice_KeepsTheGatewayZone()
    {
        // The device may be the gateway's own LAN address, whose services are reached in the Gateway zone.
        var e = BlockingExceptions.Validate(new BlockingException
        {
            Direction = ExceptionDirection.Inbound, Device = "192.168.1.1", Protocol = ExceptionProtocol.Udp, PortFrom = 51820, PortTo = 51820,
        });

        GatewayExceptionPolicies.Build([e], State, "ext", SiteZones, true)
            .Where(s => s.SourceZoneId == "ext").Select(s => s.DestinationZoneId).Should().Equal("lan", "gw");
    }

    [Fact]
    public void ConsolePolicies_WithNoSubnetsKnown_MatchEveryZoneAsBefore()
    {
        // Zones without subnets are what an unreadable network list gives: every zone, as before scoping.
        var e = BlockingExceptions.Validate(Outbound("192.168.1.50", null));
        var unscoped = new ExceptionZone[] { new("lan", false), new("iot", false), new("gw", true) };

        GatewayExceptionPolicies.Build([e], State, "ext", unscoped, true).Select(s => s.SourceZoneId == "ext" ? s.DestinationZoneId : s.SourceZoneId)
            .Distinct().Should().Equal("lan", "iot");
    }

    private static ExceptionPolicySpec Spec(string key, string protocol) => new(key, "ext", "lan",
        UniFiGroupBlockPolicyWrite.CreateAllow("n", "d", "IPV4", protocol,
            UniFiGroupBlockPolicyWrite.Endpoint("ext", null, "g", null, true), UniFiGroupBlockPolicyWrite.Endpoint("lan", null, null, null, false)));

    [Fact]
    public void Plan_KeepsUnchanged_UpdatesChanged_CreatesNew_DeletesGone()
    {
        var same = Spec("a", "tcp");
        var changed = Spec("b", "udp");
        var added = Spec("c", "tcp");
        var current = new List<GatewayExceptionPolicyRef>
        {
            new("id-a", "a", same.Hash),
            new("id-b", "b", Spec("b", "tcp").Hash),
            new("id-x", "x", "old"),
            new("id-gone", "d", "old"),
        };

        var plan = GatewayExceptionPolicies.Plan(current, new HashSet<string> { "id-a", "id-b", "id-x" }, [same, changed, added]);

        plan.Keep.Select(r => r.Id).Should().Equal("id-a");
        plan.Update.Should().ContainSingle().Which.Should().Be((current[1], changed));
        plan.Create.Should().Equal(added);
        // An unwanted policy still on the console is deleted; one already gone is not.
        plan.Delete.Select(r => r.Id).Should().Equal("id-x");
    }

    [Fact]
    public void Plan_AKeyRecordedBeforeHashes_IsUpdatedNotRecreated()
    {
        var spec = Spec("a", "tcp");

        var plan = GatewayExceptionPolicies.Plan([new GatewayExceptionPolicyRef("id-a", "a")], new HashSet<string> { "id-a" }, [spec]);

        plan.Update.Should().ContainSingle();
        plan.Create.Should().BeEmpty();
        plan.Delete.Should().BeEmpty();
    }

    private static UniFiFirewallPolicy Policy(string id, long index) =>
        new() { Id = id, Index = index, Source = new() { ZoneId = "ext" }, Destination = new() { ZoneId = "lan" } };

    [Fact]
    public void Order_PutsOwnBlocksThenExceptionsThenTheLists_AndLeavesTheRest()
    {
        var policies = new[] { Policy("user", 10000), Policy("feeds", 10001), Policy("exc", 10002), Policy("own", 10003), Policy("after", 40000) };

        var order = GatewayExceptionPolicies.Order(policies, "ext", "lan",
            new HashSet<string> { "own" }, new HashSet<string> { "exc" }, new HashSet<string> { "feeds" });

        order.Should().NotBeNull();
        order!.Value.Before.Should().Equal("own", "exc", "feeds", "user");
        order.Value.After.Should().Equal("after");
    }

    [Fact]
    public void Order_IsNullWhenAlreadyInOrder()
    {
        var policies = new[] { Policy("own", 10000), Policy("exc", 10001), Policy("feeds", 10002), Policy("user", 10003) };

        GatewayExceptionPolicies.Order(policies, "ext", "lan",
            new HashSet<string> { "own" }, new HashSet<string> { "exc" }, new HashSet<string> { "feeds" }).Should().BeNull();
    }
}

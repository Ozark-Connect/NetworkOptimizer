using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class TrustedSourcesTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static TrustedAddress Seen(string address, DateTime lastSeen, DateTime? replacedAt = null) =>
        new() { Address = address, FirstSeen = lastSeen.AddDays(-30), LastSeen = lastSeen, ReplacedAt = replacedAt };

    [Fact]
    public void Observe_FailedRead_ChangesNothing()
    {
        TrustedAddressBook.Observe([Seen("203.0.113.10", Now.AddDays(-400))], [], Now).Should().BeNull();
    }

    [Fact]
    public void Observe_NewAddress_IsAdded()
    {
        var result = TrustedAddressBook.Observe([], ["203.0.113.10"], Now)!;
        result.Should().ContainSingle(a => a.Address == "203.0.113.10" && a.FirstSeen == Now && a.ReplacedAt == null);
    }

    [Fact]
    public void Observe_ChangedAddress_KeepsTheOldOneMarkedReplaced()
    {
        var result = TrustedAddressBook.Observe([Seen("203.0.113.10", Now.AddMinutes(-10))], ["203.0.113.20"], Now)!;
        result.Should().HaveCount(2);
        result.Single(a => a.Address == "203.0.113.10").ReplacedAt.Should().Be(Now);
        result.Single(a => a.Address == "203.0.113.20").ReplacedAt.Should().BeNull();
    }

    [Fact]
    public void Observe_ReplacedAddress_StaysForSevenDays()
    {
        var stored = new[] { Seen("203.0.113.10", Now.AddDays(-6), replacedAt: Now.AddDays(-6)), Seen("203.0.113.20", Now) };
        TrustedAddressBook.Observe(stored, ["203.0.113.20"], Now)!.Should().Contain(a => a.Address == "203.0.113.10" && a.ReplacedAt == Now.AddDays(-6));
    }

    [Fact]
    public void Observe_ReplacedAddress_DropsAfterSevenDays()
    {
        var stored = new[] { Seen("203.0.113.10", Now.AddDays(-8), replacedAt: Now.AddDays(-8)), Seen("203.0.113.20", Now) };
        var result = TrustedAddressBook.Observe(stored, ["203.0.113.20"], Now)!;
        result.Select(a => a.Address).Should().Equal("203.0.113.20");
    }

    [Fact]
    public void Observe_ReturningAddress_IsCurrentAgain()
    {
        var result = TrustedAddressBook.Observe([Seen("203.0.113.10", Now.AddDays(-3), replacedAt: Now.AddDays(-3))], ["203.0.113.10"], Now)!;
        result.Single().ReplacedAt.Should().BeNull();
        result.Single().LastSeen.Should().Be(Now);
    }

    [Fact]
    public void Observe_EveryRead_RefreshesLastSeen()
    {
        TrustedAddressBook.Observe([Seen("203.0.113.10", Now.AddMinutes(-10))], ["203.0.113.10"], Now)!
            .Single().LastSeen.Should().Be(Now);
    }

    // Shapes from the main site's gateway captures: WAN IPv6 as bare addresses, PD prefixes plural
    // in ipv6_subnets, static prefixes singular in ipv6_subnet, Starlink's IPv4 on CGNAT.
    private const string GatewayJson = """
    {
      "type": "uxg",
      "ip": "198.51.100.7",
      "wan1": { "name": "eth6", "up": true, "ip": "198.51.100.7", "ipv6": ["fe80::1"] },
      "wan2": { "name": "eth0", "up": true, "ip": "100.90.141.99", "ipv6": ["2001:db8:6b00:7c2c::9d70", "fe80::2"] },
      "wan3": { "name": "gre1", "up": false, "ip": "10.108.129.149", "ipv6": [] },
      "wan4": { "name": "eth5", "up": false, "ip": "203.0.113.44", "ipv6": "2001:db8:aaaa::1" },
      "network_table": [
        { "name": "Main", "ipv6_subnets": ["2001:db8:6b00:7c2c::1/64"] },
        { "name": "Management", "ipv6_subnet": "fd3a:7c41:9e2b:99::1/64" },
        { "name": "Servers", "ipv6_subnet": "2001:db8:cafe:10::1/64" },
        { "name": "IoT" }
      ]
    }
    """;

    [Fact]
    public void FromGateways_ReadsEveryWanAndPublicPrefix()
    {
        var gateway = JsonSerializer.Deserialize<UniFiDeviceResponse>(GatewayJson)!;

        TrustedAddressBook.FromGateways([gateway]).Should().BeEquivalentTo(
            "198.51.100.7",                 // wan1 and the gateway's own address
            "203.0.113.44",                 // a WAN that is down still has its address
            "2001:db8:6b00:7c2c::9d70",     // Starlink's global IPv6; its CGNAT IPv4 is skipped
            "2001:db8:aaaa::1",             // a single string where an array is expected
            "2001:db8:6b00:7c2c::/64",      // PD prefix, masked to the network
            "2001:db8:cafe:10::/64");       // static public prefix; the ULA one is skipped
    }

    [Fact]
    public void UnexpectedIpv6Shape_DoesNotDropTheWan()
    {
        var gateway = JsonSerializer.Deserialize<UniFiDeviceResponse>(
            """{ "type": "uxg", "wan1": { "ip": "198.51.100.7", "ipv6": { "odd": true } }, "network_table": [ { "ipv6_subnets": 5 } ] }""")!;

        gateway.GetWanInterfaces().Should().ContainSingle(w => w.Ip == "198.51.100.7");
        TrustedAddressBook.FromGateways([gateway]).Should().Equal("198.51.100.7");
    }
}

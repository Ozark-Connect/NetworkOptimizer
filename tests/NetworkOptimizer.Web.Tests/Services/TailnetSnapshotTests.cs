using FluentAssertions;
using NetworkOptimizer.Monitoring.Tailscale;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Tailscale;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// How a site's tailnet is assembled from an API device list and a local tailscaled status, and what
/// the pages read off it. Addresses are documentation ranges.
/// </summary>
public class TailnetSnapshotTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 17, 0, 0, DateTimeKind.Utc);

    private static TailscaleLocalPeer Local(string ip, string? dns = null, string? curAddr = null, string? relay = null,
        bool active = false, DateTime? handshake = null, IReadOnlyList<string>? routes = null, bool exit = false,
        IReadOnlyList<string>? addrs = null) =>
        new(dns, "host", "linux", "user1@example.com", [ip], curAddr, relay, null, 100, 200, handshake, true, active,
            exit, routes ?? [], addrs ?? [], []);

    private static TailnetDevice Device(string name, string ip, IReadOnlyList<string>? routes = null, IReadOnlyList<string>? endpoints = null) =>
        new(name, name, "linux", "user1@example.com", [ip, "fd7a:115c:a1e0::9"], [], routes ?? [], endpoints ?? [], true);

    [Fact]
    public void Api_device_takes_the_local_path_of_the_same_node()
    {
        var local = new TailscaleLocalStatus("Running", Local("100.100.1.1"),
            [Local("100.100.1.2", "phone.tail0000.ts.net", curAddr: "198.51.100.7:41641", relay: "dfw", active: true)]);

        var snapshot = TailnetSnapshot.Build([Device("phone", "100.100.1.2")], local, null, Now);

        var phone = snapshot.Peer("fd7a:115c:a1e0::9")!;
        phone.Name.Should().Be("phone");
        phone.Path!.Kind.Should().Be(TailnetPathKind.Direct);
        phone.Path.Endpoint.Should().Be("198.51.100.7:41641");
        phone.RxBytes.Should().Be(100);
    }

    [Fact]
    public void Local_status_alone_names_peers_by_machine_name()
    {
        var local = new TailscaleLocalStatus("Running", Local("100.100.1.1"), [Local("100.100.1.2", "laptop.tail0000.ts.net.")]);

        TailnetSnapshot.Build(null, local, null, Now).NameFor("100.100.1.2").Should().Be("laptop");
    }

    [Fact]
    public void The_local_node_is_self_not_a_peer()
    {
        var local = new TailscaleLocalStatus("Running", Local("100.100.1.1", addrs: ["192.168.1.10:41641"]), []);

        var snapshot = TailnetSnapshot.Build([Device("nas", "100.100.1.1", routes: ["0.0.0.0/0", "::/0", "192.0.2.0/24"])], local, null, Now);

        snapshot.Peers.Should().BeEmpty();
        snapshot.Self!.Name.Should().Be("nas");
        snapshot.RouterAtLanAddress("192.168.1.10")!.RouterBadge.Should().Be("Tailscale exit node and subnet router");
    }

    [Theory]
    [InlineData(null, null, true, TailnetPathKind.Derp)]
    [InlineData(null, null, false, null)]
    [InlineData("198.51.100.7:41641", null, false, TailnetPathKind.Direct)]
    [InlineData(null, "192.0.2.5:7777:3", false, TailnetPathKind.PeerRelay)]
    public void Path_kind(string? curAddr, string? peerRelay, bool active, TailnetPathKind? expected)
    {
        var peer = new TailscaleLocalPeer(null, "h", null, null, ["100.100.1.2"], curAddr, "dfw", peerRelay, 0, 0, null,
            true, active, false, [], [], []);

        TailnetSnapshot.PathOf(peer, Now)?.Kind.Should().Be(expected);
    }

    [Fact]
    public void Router_badge_needs_a_route_and_a_lan_endpoint()
    {
        var snapshot = TailnetSnapshot.Build(
            [
                Device("router", "100.100.1.5", routes: ["192.0.2.0/24"], endpoints: ["192.168.1.20:41641", "203.0.113.5:41641"]),
                Device("plain", "100.100.1.6", endpoints: ["192.168.1.21:41641"]),
            ], null, null, Now);

        snapshot.RouterAtLanAddress("192.168.1.20")!.RouterBadge.Should().Be("Tailscale subnet router");
        snapshot.RouterAtLanAddress("203.0.113.5").Should().BeNull("a public endpoint is not a LAN host");
        snapshot.RouterAtLanAddress("192.168.1.21").Should().BeNull("a node that routes nothing gets no badge");
    }

    [Fact]
    public void Snat_lookup_matches_router_tuple()
    {
        var snapshot = TailnetSnapshot.Build(null, null,
            [new TailnetSnatEntry("udp", "100.100.1.2", 5000, "198.51.100.20", 53, "192.0.2.10", 6001)], Now);

        snapshot.SnatSource("UDP", "192.0.2.10", 6001, "198.51.100.20", 53).Should().Be("100.100.1.2");
        snapshot.SnatSource("udp", "192.0.2.10", 6002, "198.51.100.20", 53).Should().BeNull();
    }

    [Fact]
    public void Path_label_and_detail()
    {
        var derp = new TailnetPathInfo { Kind = TailnetPathKind.Derp, Relay = "dfw", LastHandshake = Now.AddSeconds(-40), ReadAt = Now };

        TailnetDisplay.PathLabel(derp).Should().Be("Relayed (DERP dfw)");
        TailnetDisplay.PathDetail(derp).Should().Contain("40 seconds before this was read");
    }
}

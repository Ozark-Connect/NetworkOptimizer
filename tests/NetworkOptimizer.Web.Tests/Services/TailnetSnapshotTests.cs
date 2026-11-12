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

    [Theory]
    [InlineData("http://192.168.1.5:8080", null, true)]
    [InlineData("https://10.0.0.2", null, true)]
    [InlineData("http://127.0.0.1:8080", null, true)]
    [InlineData("https://[fd00::5]:443", null, true)]
    [InlineData("https://203.0.113.10", null, false)]
    [InlineData("https://headscale.example.com", "203.0.113.10", false)]
    [InlineData("https://headscale.example.com", "192.168.1.5", true)]
    [InlineData("https://headscale.lan", "", true)]
    public async Task A_Headscale_inside_the_site_goes_through_its_tunnel(string url, string? resolvesTo, bool onSite)
    {
        Task<System.Net.IPAddress[]> Resolve(string _) => resolvesTo is { Length: > 0 }
            ? Task.FromResult(new[] { System.Net.IPAddress.Parse(resolvesTo) })
            : throw new System.Net.Sockets.SocketException(11001);

        (await TailscaleDirectoryRegistry.HeadscaleOnSiteAsync(url, Resolve)).Should().Be(onSite);
    }

    [Fact]
    public void Traffic_reads_from_the_devices_side()
    {
        // The host received 1 MB from the device and sent it 10 MB: the device received 10 MB.
        var peer = new TailnetPeer { Name = "phone", RxBytes = 1L << 20, TxBytes = 10L << 20 };

        TailnetDisplay.Traffic(peer).Should().Be("Received 10 MB, sent 1 MB");
    }

    [Fact]
    public void Relayed_path_names_its_DERP_region_from_the_map()
    {
        var peer = new TailscaleLocalPeer(null, "h", null, null, ["100.100.1.2"], null, "dfw", null, 0, 0, null,
            true, true, false, [], [], []);
        var derp = new DerpMapClient.DerpMap(new Dictionary<string, DerpRegion>(),
            new Dictionary<string, DerpRegion> { ["dfw"] = new DerpRegion("dfw", "Dallas") });

        var path = TailnetSnapshot.Build(null, new TailscaleLocalStatus("Running", null, [peer]), null, Now, derp).Peers.Single().Path!;

        TailnetDisplay.PathLabel(path).Should().Be("Relayed (DERP Dallas)");
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
    public void A_shared_in_node_missing_from_the_device_list_comes_from_local_status()
    {
        var local = new TailscaleLocalStatus("Running", Local("100.100.1.1"),
            [Local("100.100.1.9", "shared.tail1111.ts.net.", curAddr: "203.0.113.5:41641", active: true), Local("100.100.1.10")]);

        var snapshot = TailnetSnapshot.Build([Device("phone", "100.100.1.2")], local, null, Now);

        snapshot.NameFor("100.100.1.9").Should().Be("shared");
        snapshot.IsPeer("100.100.1.10").Should().BeFalse("sharing's placeholder peer has no MagicDNS name and is no device");
        snapshot.Peers.Should().HaveCount(2);
    }

    [Fact]
    public void A_live_direct_path_outranks_an_offline_peers_stale_endpoint()
    {
        // Both nodes sit behind one NAT. The offline one's device record still lists the address and port.
        var stale = Device("offline", "100.100.1.2", endpoints: ["203.0.113.5:41641"]) with { Online = false };
        var live = Local("100.100.1.9", "live.tail1111.ts.net.", curAddr: "203.0.113.5:41641", active: true);
        var snapshot = TailnetSnapshot.Build([stale], new TailscaleLocalStatus("Running", Local("100.100.1.1"), [live]), null, Now);

        snapshot.PeerAtEndpoint("203.0.113.5", 41641, addressAlone: false)!.Name.Should().Be("live");
        snapshot.PeerAtEndpoint("203.0.113.5", 50000, addressAlone: true)!.Name.Should().Be("live");
    }

    [Fact]
    public void An_offline_peers_listed_endpoint_names_nobody()
    {
        var stale = Device("offline", "100.100.1.2", endpoints: ["203.0.113.5:41641"]) with { Online = false };

        TailnetSnapshot.Build([stale], null, null, Now).PeerAtEndpoint("203.0.113.5", 41641, addressAlone: true).Should().BeNull();
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
        TailnetDisplay.PathDetail(derp).Should().Contain("handshake 40 seconds before the read");
    }

    private static NetworkOptimizer.Web.Services.Monitoring.LiveTraffic.LiveFlowRow Row(string key, string? peer, long total, string? role = null, double bps = 0, string dest = "198.51.100.7") => new()
    {
        Key = key,
        ClientIp = "192.168.1.10",
        DestinationIp = dest,
        RemotePort = 41641,
        TailnetEndpointPeer = peer,
        TailnetRole = role,
        TotalDownBytes = total,
        DownBps = bps,
    };

    [Fact]
    public void Busiest_endpoint_flow_is_direct_the_rest_are_probes()
    {
        var rows = NetworkOptimizer.Web.Services.Monitoring.LiveTraffic.LiveTrafficStore.MarkTailnetPaths(
        [
            Row("a", "phone", 5_000_000),
            Row("b", "phone", 900),
            Row("c", "laptop", 700),
            Row("d", null, 9_000_000),
        ]);

        rows.Single(r => r.Key == "a").DestinationSub.Should().Be("Direct");
        rows.Single(r => r.Key == "b").DestinationSub.Should().Be("Path probe");
        rows.Single(r => r.Key == "c").TailnetRole.Should().BeNull("probes alone carry no traffic, so nothing is called the path");
        rows.Single(r => r.Key == "d").TailnetRole.Should().BeNull();
    }

    [Fact]
    public void Observer_reads_direct_off_the_marked_flow()
    {
        var path = TailnetFlowObserver.Direct([Row("a", "phone", 5_000_000, "Direct")], "phone", Now)!;

        path.Kind.Should().Be(TailnetPathKind.Direct);
        path.Endpoint.Should().Be("198.51.100.7:41641");
        path.Observed.Should().BeTrue();
        TailnetDisplay.PathLabel(path, afterTest: true).Should().Be("Direct (seen just after the test)");
        TailnetDisplay.PathLabel(path).Should().Be("Direct");
    }

    [Fact]
    public void Observer_calls_relayed_only_with_a_busy_relay_and_no_endpoint_traffic()
    {
        var busyRelay = Row("r", null, 1_000_000, "DERP relay", bps: 500_000, dest: "203.0.113.9");

        TailnetFlowObserver.Relayed([busyRelay], "phone", TailnetSnapshot.Empty, Now)!.Kind.Should().Be(TailnetPathKind.Derp);
        TailnetFlowObserver.Relayed([busyRelay, Row("a", "phone", 5_000_000)], "phone", TailnetSnapshot.Empty, Now).Should().BeNull();
        TailnetFlowObserver.Relayed([Row("r", null, 1_000, "DERP relay", bps: 100)], "phone", TailnetSnapshot.Empty, Now).Should().BeNull();
    }

    [Fact]
    public void Owner_falls_back_to_tags_and_shortens()
    {
        TailnetDisplay.Owner(new TailnetPeer { Name = "nas", Tags = ["tag:server"] }).Should().Be("tag:server");
        TailnetDisplay.ShortOwner("someone-with-a-long-name@example.com").Should().HaveLength(24).And.EndWith("…");
    }
}

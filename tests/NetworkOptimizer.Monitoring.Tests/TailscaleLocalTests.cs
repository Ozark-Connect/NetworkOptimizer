using FluentAssertions;
using NetworkOptimizer.Monitoring.Tailscale;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// The local tailscaled readers: the LocalAPI status JSON (shape from tailscaled 1.96's
/// ipnstate.Status) and the conntrack lines a subnet router or exit node leaves.
/// </summary>
public class TailscaleLocalTests
{
    private const string StatusJson = """
        {
          "Version": "1.96.5",
          "BackendState": "Running",
          "TailscaleIPs": ["100.100.1.1", "fd7a:115c:a1e0::1"],
          "Self": {
            "HostName": "server", "DNSName": "server.tail0000.ts.net.", "OS": "linux", "UserID": 11,
            "TailscaleIPs": ["100.100.1.1", "fd7a:115c:a1e0::1"], "Addrs": ["192.0.2.10:41641", "203.0.113.5:41641"],
            "CurAddr": "", "Relay": "dfw", "PeerRelay": "", "RxBytes": 0, "TxBytes": 0,
            "LastHandshake": "0001-01-01T00:00:00Z", "Online": true, "ExitNode": false, "ExitNodeOption": false,
            "PrimaryRoutes": ["192.0.2.0/24"], "Active": false
          },
          "Peer": {
            "nodekey:aaaa": {
              "HostName": "localhost", "DNSName": "phone.tail0000.ts.net.", "OS": "iOS", "UserID": 22,
              "TailscaleIPs": ["100.100.1.2", "fd7a:115c:a1e0::2"], "Addrs": null,
              "CurAddr": "198.51.100.7:40000", "Relay": "dfw", "PeerRelay": "", "RxBytes": 30000000, "TxBytes": 250000000,
              "LastHandshake": "2026-11-11T16:40:29.520059184Z", "Online": true, "ExitNodeOption": false, "Active": true
            },
            "nodekey:bbbb": {
              "HostName": "laptop", "DNSName": "laptop.tail0000.ts.net.", "OS": "windows", "UserID": 11,
              "TailscaleIPs": ["100.100.1.3"], "CurAddr": "", "Relay": "ord", "PeerRelay": "",
              "RxBytes": 10, "TxBytes": 20, "LastHandshake": "2026-11-11T16:39:00Z", "Online": true, "Active": true
            },
            "nodekey:cccc": {
              "HostName": "device-of-shared-to-user", "DNSName": "", "OS": "", "UserID": 33,
              "TailscaleIPs": ["100.100.1.4"], "CurAddr": "", "Relay": "", "PeerRelay": "",
              "LastHandshake": "0001-01-01T00:00:00Z", "Online": false, "Active": false
            }
          },
          "User": {
            "11": { "ID": 11, "LoginName": "admin@example.com", "DisplayName": "Admin" },
            "22": { "ID": 22, "LoginName": "user1@example.com", "DisplayName": "User1" }
          }
        }
        """;

    [Fact]
    public void Parses_self_and_peers()
    {
        var status = TailscaleStatusParser.Parse(StatusJson);

        status.BackendState.Should().Be("Running");
        status.Self!.DnsName.Should().Be("server.tail0000.ts.net");
        status.Self.PrimaryRoutes.Should().Equal("192.0.2.0/24");
        status.Peers.Should().HaveCount(3);

        var phone = status.Peers.Single(p => p.Os == "iOS");
        phone.User.Should().Be("user1@example.com");
        phone.CurAddr.Should().Be("198.51.100.7:40000");
        phone.RxBytes.Should().Be(30000000);
        phone.LastHandshake.Should().BeCloseTo(new DateTime(2026, 11, 11, 16, 40, 29, 520, DateTimeKind.Utc), TimeSpan.FromMilliseconds(1));
        phone.LastHandshake!.Value.Kind.Should().Be(DateTimeKind.Utc);
        phone.Addrs.Should().BeEmpty();
    }

    [Fact]
    public void Zero_time_and_blank_fields_read_as_absent()
    {
        var shared = TailscaleStatusParser.Parse(StatusJson).Peers.Single(p => p.TailscaleIps.Contains("100.100.1.4"));

        shared.DnsName.Should().BeNull();
        shared.Os.Should().BeNull();
        shared.User.Should().BeNull();
        shared.Relay.Should().BeNull();
        shared.LastHandshake.Should().BeNull();
    }

    [Fact]
    public void Status_round_trips_as_the_agent_sends_it()
    {
        var status = TailscaleStatusParser.Parse(StatusJson);
        var json = System.Text.Json.JsonSerializer.Serialize(status);
        var back = System.Text.Json.JsonSerializer.Deserialize<TailscaleLocalStatus>(json)!;

        back.Peers.Should().HaveCount(3);
        back.Self!.TailscaleIps.Should().Equal(status.Self!.TailscaleIps);
    }

    [Fact]
    public void Snat_line_maps_router_port_to_peer()
    {
        const string line = "ipv4     2 tcp      6 431999 ESTABLISHED src=100.100.1.2 dst=198.51.100.20 sport=51515 dport=443 "
            + "src=198.51.100.20 dst=192.0.2.10 sport=443 dport=40001 [ASSURED] mark=0 zone=0 use=2";

        TailnetSnat.ParseLine(line).Should().Be(
            new TailnetSnatEntry("tcp", "100.100.1.2", 51515, "198.51.100.20", 443, "192.0.2.10", 40001));
    }

    [Theory]
    // Peer to peer inside the tailnet: not translated.
    [InlineData("ipv4 2 udp 17 29 src=100.100.1.2 dst=100.100.1.3 sport=41641 dport=41641 src=100.100.1.3 dst=100.100.1.2 sport=41641 dport=41641 mark=0 use=1")]
    // A tailnet source the host did not NAT (--snat-subnet-routes=false).
    [InlineData("ipv4 2 tcp 6 300 ESTABLISHED src=100.100.1.2 dst=192.0.2.50 sport=51515 dport=22 src=192.0.2.50 dst=100.100.1.2 sport=22 dport=51515 [ASSURED] mark=0 use=1")]
    // Not a tailnet source.
    [InlineData("ipv4 2 tcp 6 300 ESTABLISHED src=192.0.2.30 dst=198.51.100.20 sport=51515 dport=443 src=198.51.100.20 dst=192.0.2.30 sport=443 dport=51515 [ASSURED] mark=0 use=1")]
    // ICMP has no ports to match on.
    [InlineData("ipv4 2 icmp 1 29 src=100.100.1.2 dst=198.51.100.20 type=8 code=0 id=7 src=198.51.100.20 dst=192.0.2.10 type=0 code=0 id=7 mark=0 use=1")]
    public void Snat_skips_lines_with_nothing_to_attribute(string line)
    {
        TailnetSnat.ParseLine(line).Should().BeNull();
    }

    [Fact]
    public void Snat_parse_keeps_only_translated_tailnet_sources()
    {
        var table = string.Join('\n',
            "ipv4 2 tcp 6 300 ESTABLISHED src=192.0.2.30 dst=198.51.100.20 sport=1 dport=443 src=198.51.100.20 dst=192.0.2.30 sport=443 dport=1 mark=0 use=1",
            "ipv6 10 udp 17 29 src=fd7a:115c:a1e0::2 dst=2001:db8::20 sport=5000 dport=443 src=2001:db8::20 dst=2001:db8:1::10 sport=443 dport=6000 mark=0 use=1",
            "ipv4 2 udp 17 29 src=100.100.1.2 dst=198.51.100.20 sport=5000 dport=53 src=198.51.100.20 dst=192.0.2.10 sport=53 dport=6001 mark=0 use=1");

        var entries = TailnetSnat.Parse(new StringReader(table));

        entries.Select(e => (e.PeerIp, e.NatPort)).Should().Equal(("fd7a:115c:a1e0::2", 6000), ("100.100.1.2", 6001));
    }
}

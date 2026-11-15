using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class IpsAlertLineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 55, 30, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> HomeNet = ["192.168.1.1/24", "192.168.64.1/24"];

    // The shape the UXG-Fiber's syslog-ng writes, addresses replaced with test values.
    private const string Outbound =
        "2026-11-15T09:55:29-06:00 Gateway Gateway ubnt-idsips-daemon[3196]: {\"alert\":{\"action\":\"blocked\",\"category\":\"Malware Command and Control Activity Detected\",\"gid\":1,"
        + "\"metadata\":{\"affected_product\":[\"Windows_XP_Vista_7_8_10_Server_32_64_Bit\"],\"attack_target\":[\"Client_Endpoint\"],\"confidence\":[\"High\"],\"ubnt_category\":[\"MALWARE\"]},"
        + "\"rev\":3,\"severity\":1,\"signature\":\"ET MALWARE X2000M.Agent Checkin Jan 24 2017\",\"signature_id\":2023764},\"app_proto\":\"http\",\"dest_ip\":\"203.0.113.10\",\"dest_port\":80,"
        + "\"direction\":\"to_server\",\"dst_mac\":\"00:11:22:33:44:55\",\"event_type\":\"alert\",\"flow\":{\"bytes_toclient\":1209,\"dest_ip\":\"203.0.113.10\",\"dest_port\":80,\"src_ip\":\"192.168.1.50\",\"src_port\":36114},"
        + "\"flow_id\":531270233352920,\"host\":\"usg-sensor\",\"http\":{\"hostname\":\"example.com\",\"http_user_agent\":\"v7v7v7v7v7v7v7v7v7v7v7v7\"},\"ip_v\":4,\"pkt_src\":\"wire/pcap\","
        + "\"proto\":\"TCP\",\"src_ip\":\"192.168.1.50\",\"src_mac\":\"AA:BB:CC:DD:EE:FF\",\"src_port\":36114,\"timestamp\":\"2026-11-15T09:55:29.550330-0600\","
        + "\"ubnt\":{\"suricata_version\":\"8.0.2\",\"ubnt_category\":\"MALWARE\"}}";

    private static IpsAlert? Parse(string line, out IpsAlertLine.Reject reject) => IpsAlertLine.Parse(line, HomeNet, Now, out reject);

    [Fact]
    public void OutboundAlert_Parses()
    {
        var alert = Parse(Outbound, out var reject)!;
        reject.Should().Be(IpsAlertLine.Reject.None);
        alert.ClientIp.Should().Be("192.168.1.50");
        alert.ClientMac.Should().Be("aa:bb:cc:dd:ee:ff");
        alert.RemoteIp.Should().Be("203.0.113.10");
        alert.RemotePort.Should().Be(80);
        alert.Protocol.Should().Be("tcp");
        alert.SignatureId.Should().Be(2023764);
        alert.Category.Should().Be("Malware Command and Control Activity Detected");
        alert.UbntCategory.Should().Be("MALWARE");
        alert.Severity.Should().Be(1);
        alert.Confidence.Should().Be("High");
        alert.AttackTargets.Should().Equal("Client_Endpoint");
        alert.Hostname.Should().Be("example.com");
        alert.Action.Should().Be("blocked");
        alert.TimestampUnixMs.Should().Be(new DateTimeOffset(2026, 11, 15, 15, 55, 29, 550, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ResponseDirectionRule_StillNamesTheInitiatorAsTheClient()
    {
        // A to_client rule reports the internet end as src; flow keeps the initiator.
        var swapped = Outbound
            .Replace("\"dest_ip\":\"203.0.113.10\",\"dest_port\":80,\"direction\"", "\"dest_ip\":\"192.168.1.50\",\"dest_port\":36114,\"direction\"")
            .Replace("\"src_ip\":\"192.168.1.50\",\"src_mac\"", "\"src_ip\":\"203.0.113.10\",\"src_mac\"")
            .Replace("\"src_port\":36114,\"timestamp\"", "\"src_port\":80,\"timestamp\"");
        var alert = Parse(swapped, out _)!;
        alert.ClientIp.Should().Be("192.168.1.50");
        alert.RemoteIp.Should().Be("203.0.113.10");
        alert.RemotePort.Should().Be(80);
        alert.ClientMac.Should().Be("00:11:22:33:44:55");
    }

    [Fact]
    public void InboundAlert_IsNotOutbound()
    {
        var inbound = Outbound
            .Replace("\"src_ip\":\"192.168.1.50\",\"src_port\":36114},", "\"src_ip\":\"198.51.100.7\",\"src_port\":36114},")
            .Replace("\"flow\":{\"bytes_toclient\":1209,\"dest_ip\":\"203.0.113.10\"", "\"flow\":{\"bytes_toclient\":1209,\"dest_ip\":\"192.168.1.50\"")
            .Replace("\"dest_ip\":\"203.0.113.10\",\"dest_port\":80,\"direction\"", "\"dest_ip\":\"192.168.1.50\",\"dest_port\":80,\"direction\"")
            .Replace("\"src_ip\":\"192.168.1.50\",\"src_mac\"", "\"src_ip\":\"198.51.100.7\",\"src_mac\"");
        Parse(inbound, out var reject).Should().BeNull();
        reject.Should().Be(IpsAlertLine.Reject.NotOutbound);
    }

    [Fact]
    public void GlobalIPv6Client_IsLocalOnlyThroughHomeNet()
    {
        var v6 = Outbound.Replace("192.168.1.50", "2001:db8:1::50").Replace("203.0.113.10", "2001:db8:ffff::10");
        IpsAlertLine.Parse(v6, ["2001:db8:1::/64"], Now, out _)!.ClientIp.Should().Be("2001:db8:1::50");
        IpsAlertLine.Parse(v6, HomeNet, Now, out var reject).Should().BeNull();
        reject.Should().Be(IpsAlertLine.Reject.NotOutbound);
    }

    [Theory]
    [InlineData("2026-11-15T09:55:29-06:00 Gateway Gateway sshd[12]: {\"event_type\":\"alert\"}", IpsAlertLine.Reject.NotIpsDaemon)]
    [InlineData("2026-11-15T09:55:29-06:00 Gateway Gateway ubnt-idsips-daemon[3196]: {\"alert\":", IpsAlertLine.Reject.NotJson)]
    [InlineData("2026-11-15T09:55:29-06:00 Gateway Gateway ubnt-idsips-daemon[3196]: {\"event_type\":\"stats\"}", IpsAlertLine.Reject.NotAlert)]
    public void ForeignOrBrokenLines_AreRejected(string line, IpsAlertLine.Reject expected)
    {
        Parse(line, out var reject).Should().BeNull();
        reject.Should().Be(expected);
    }

    [Fact]
    public void IncompleteAlert_IsRejected()
    {
        Parse(Outbound.Replace("\"gid\":1,", "\"gid\":3,"), out var reject).Should().BeNull();
        reject.Should().Be(IpsAlertLine.Reject.IncompleteAlert);
        Parse(Outbound.Replace("\"signature_id\":2023764", "\"signature_id\":0"), out reject).Should().BeNull();
        reject.Should().Be(IpsAlertLine.Reject.IncompleteAlert);
    }

    [Fact]
    public void MismatchedAddresses_AreRejected()
    {
        Parse(Outbound.Replace("\"src_ip\":\"192.168.1.50\",\"src_mac\"", "\"src_ip\":\"192.168.1.99\",\"src_mac\""), out var reject).Should().BeNull();
        reject.Should().Be(IpsAlertLine.Reject.BadAddresses);
    }

    [Fact]
    public void StaleAlert_IsRejected()
    {
        IpsAlertLine.Parse(Outbound, HomeNet, Now.AddMinutes(5), out var reject).Should().BeNull();
        reject.Should().Be(IpsAlertLine.Reject.Stale);
    }

    [Fact]
    public void HomeNet_IsReadFromTheYaml()
    {
        const string yaml = "%YAML 1.1\n---\nvars:\n  address-groups:\n    HOME_NET: \"[192.168.1.1/24,192.168.64.1/24,fd00::1/64]\"\n    EXTERNAL_NET: \"!$HOME_NET\"\n";
        IpsAlertLine.ParseHomeNet(yaml).Should().Equal("192.168.1.1/24", "192.168.64.1/24", "fd00::1/64");
        IpsAlertLine.ParseHomeNet(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("2026-11-15T09:55:29.550330-0600", 2026, 11, 15, 15, 55, 29)]
    [InlineData("2026-11-15T15:55:29Z", 2026, 11, 15, 15, 55, 29)]
    [InlineData("2026-11-15T10:55:29+05:00", 2026, 11, 15, 5, 55, 29)]
    public void Timestamps_ParseWithOrWithoutTheOffsetColon(string value, int y, int mo, int d, int h, int mi, int s) =>
        IpsAlertLine.ParseTimestamp(value)!.Value.UtcDateTime.Should().BeCloseTo(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc), TimeSpan.FromSeconds(1));
}

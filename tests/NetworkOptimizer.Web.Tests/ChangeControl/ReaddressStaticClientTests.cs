using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>Which connected clients the Re-address dry run lists as having no DHCP lease from this gateway.</summary>
public class ReaddressStaticClientTests
{
    private const string Lan = "n1";
    private const string Reserved = "aa:bb:cc:dd:ee:01";
    private const string Plain = "aa:bb:cc:dd:ee:02";
    private const string Leased = "aa:bb:cc:dd:ee:03";

    private static readonly Dictionary<string, JsonObject> Networks = new(StringComparer.OrdinalIgnoreCase)
    {
        [Lan] = JsonNode.Parse("""{"_id":"n1","name":"Default","ip_subnet":"192.168.1.1/24","dhcpd_enabled":true,"dhcpd_start":"192.168.1.100","dhcpd_stop":"192.168.1.200"}""")!.AsObject(),
    };

    private static readonly List<(Readdressing.Subnet Old, Readdressing.Subnet New)> Map =
        Readdressing.Map([new SubnetMove(Lan, "Default", null, "192.168.1.0/24", "10.42.1.0/24")]);

    private static JsonObject Client(string mac, string ip, bool reserved = false, double rate = 0, string? name = "Client", string network = Lan) =>
        new()
        {
            ["mac"] = mac, ["ip"] = ip, ["network_id"] = network, ["use_fixedip"] = reserved,
            ["name"] = name, ["hostname"] = "host-" + mac[^2..],
            ["tx_bytes-r"] = rate, ["rx_bytes-r"] = 0, ["wired-tx_bytes-r"] = 0, ["wired-rx_bytes-r"] = 0,
        };

    private static HashSet<string> Macs(params string[] macs) => new(macs, StringComparer.Ordinal);

    private static List<StaticClient> WithGatewayLeases(HashSet<string> leases, params JsonObject[] clients) =>
        ChangeControlService.ConnectedStaticClients(clients, leases, null, Networks, Map);

    [Fact]
    public void ReservedIdleClientWithNoLease_IsListed()
    {
        // The case the traffic split got wrong: quiet, reserved, no lease. The address is set on the device.
        var tv = Client(Reserved, "192.168.1.150", reserved: true, rate: 0, name: "Bedroom TV");

        var listed = WithGatewayLeases(Macs(Leased), tv);

        listed.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new StaticClient("Bedroom TV", Reserved, "192.168.1.150", "10.42.1.150", "Default", Reserved: true));
    }

    [Fact]
    public void TrafficRate_DoesNotChangeTheAnswer()
    {
        var quiet = Client(Reserved, "192.168.1.150", reserved: true, rate: 0);
        var busy = Client(Plain, "192.168.1.151", reserved: true, rate: 500_000);

        var listed = WithGatewayLeases(Macs(Leased), quiet, busy);

        listed.Select(c => c.Mac).Should().BeEquivalentTo([Reserved, Plain]);
    }

    [Fact]
    public void ClientWithAGatewayLease_IsNotListed_ReservedOrNot()
    {
        var reserved = Client(Reserved, "192.168.1.150", reserved: true);
        var plain = Client(Plain, "192.168.1.151");

        WithGatewayLeases(Macs(Reserved, Plain), reserved, plain).Should().BeEmpty();
    }

    [Fact]
    public void UnreservedClientWithNoLease_IsListed()
    {
        var listed = WithGatewayLeases(Macs(Leased), Client(Plain, "192.168.1.50"));

        listed.Should().ContainSingle().Which.Reserved.Should().BeFalse();
    }

    [Fact]
    public void ClientOutsideTheMovedSubnets_IsIgnored()
    {
        WithGatewayLeases(Macs(), Client(Plain, "192.168.30.50", network: "n2")).Should().BeEmpty();
    }

    [Fact]
    public void ReservationFlag_ReadsItsStringForm()
    {
        var c = Client(Reserved, "192.168.1.150");
        c["use_fixedip"] = "true";

        WithGatewayLeases(Macs(Leased), c).Should().ContainSingle().Which.Reserved.Should().BeTrue();
    }

    [Fact]
    public void Name_FallsBackToHostnameThenMac()
    {
        var byHost = Client(Plain, "192.168.1.50", name: "");
        var byMac = Client(Reserved, "192.168.1.51", name: "");
        byMac["hostname"] = "";

        var listed = WithGatewayLeases(Macs(), byHost, byMac);

        listed.Single(c => c.Mac == Plain).Name.Should().Be("host-02");
        listed.Single(c => c.Mac == Reserved).Name.Should().Be(Reserved);
    }

    [Fact]
    public void WithoutGatewayLeases_TheIpTableClearsUnreservedClientsOnly()
    {
        var reserved = Client(Reserved, "192.168.1.150", reserved: true);
        var inTable = Client(Plain, "192.168.1.151");
        var notInTable = Client(Leased, "192.168.1.50");

        var listed = ChangeControlService.ConnectedStaticClients([reserved, inTable, notInTable], null, Macs(Plain), Networks, Map);

        // A reservation shows in the IP Table like a lease, so a reserved client cannot be judged there.
        listed.Select(c => c.Mac).Should().BeEquivalentTo([Leased]);
    }

    [Fact]
    public void WithoutAnySource_AnAddressOutsideTheDhcpRangeCounts()
    {
        var inRange = Client(Plain, "192.168.1.150");
        var outside = Client(Leased, "192.168.1.50");
        var reserved = Client(Reserved, "192.168.1.51", reserved: true);

        var listed = ChangeControlService.ConnectedStaticClients([inRange, outside, reserved], null, null, Networks, Map);

        listed.Select(c => c.Mac).Should().BeEquivalentTo([Leased]);
    }

    [Fact]
    public void DryRunView_HasNoSeparateIdleGroup()
    {
        // Every connected no-lease client sits in StaticClients. A hidden group once dropped them from the plan.
        typeof(ReaddressView).GetProperty("IdleClients").Should().BeNull();
        typeof(StaticClient).GetProperty("Idle").Should().BeNull();
    }

    [Fact]
    public void ANetworkWithClientsSetOnTheDevice_NeedsAcknowledging()
    {
        var doc = new PlanDocument
        {
            Operations =
            [
                new PlanOperation { Key = "networkconf:update:Default", Collection = "networkconf", Action = PlanAction.Update, ObjectId = Lan, ObjectName = "Default" },
                new PlanOperation { Key = "networkconf:update:Other", Collection = "networkconf", Action = PlanAction.Update, ObjectId = "n2", ObjectName = "Other" },
            ],
        };
        var moves = new List<SubnetMove> { new(Lan, "Default", null, "192.168.1.0/24", "10.42.1.0/24"), new("n2", "Other", 20, "192.168.20.0/24", "10.42.20.0/24") };
        var statics = new List<StaticClient> { new("Printer", Plain, "192.168.1.20", "10.42.1.20", "Default", false), new("Stray", "aa:bb:cc:dd:ee:09", "172.16.0.5", "172.16.0.5", "", false) };

        var unmarked = ChangeControlService.MarkStaticClients(doc, moves, statics);

        var op = doc.Operations[0];
        op.Tier.Should().Be(PlanTiers.Acknowledge);
        op.Reasons.Should().ContainSingle().Which.Should().Be("Clients set on the device stay on the old subnet until you change them: Printer (192.168.1.20 to 10.42.1.20)");
        doc.Operations[1].Tier.Should().Be(PlanTiers.Direct, "no client on it is set on the device");
        unmarked.Select(s => s.Name).Should().Equal("Stray");
    }
}

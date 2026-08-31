using System.Net;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Conntrack;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Parser fixtures shaped like real /proc/net/nf_conntrack lines, and delta accounting under
/// flow churn - the same discipline the WAN reconciler got, before any deployment.
/// All addresses are RFC 5737 / documentation ranges.
/// </summary>
public class ConntrackParserTests
{
    [Fact]
    public void ParsesNattedIpv4TcpFlow()
    {
        var flow = ConntrackParser.ParseLine(
            "ipv4     2 tcp      6 431976 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport=51512 dport=443 packets=124 bytes=12345 src=203.0.113.34 dst=198.51.100.7 sport=443 dport=51512 packets=110 bytes=98765 [ASSURED] mark=0 zone=0 use=2");

        flow.Should().NotBeNull();
        flow!.OrigSrc.Should().Be(IPAddress.Parse("192.168.1.100"));
        flow.OrigDst.Should().Be(IPAddress.Parse("203.0.113.34"));
        flow.ReplySrc.Should().Be(IPAddress.Parse("203.0.113.34"));
        flow.ReplyDst.Should().Be(IPAddress.Parse("198.51.100.7"));
        flow.OrigBytes.Should().Be(12345);
        flow.ReplyBytes.Should().Be(98765);
    }

    [Fact]
    public void ParsesIcmpWithIdAsPortStandIn()
    {
        var flow = ConntrackParser.ParseLine(
            "ipv4     2 icmp     1 29 src=192.168.1.10 dst=203.0.113.1 type=8 code=0 id=1234 packets=2 bytes=168 src=203.0.113.1 dst=198.51.100.7 type=0 code=0 id=1234 packets=2 bytes=168 mark=0 use=2");

        flow.Should().NotBeNull();
        flow!.Key.Should().Contain("id1234");
        flow.OrigBytes.Should().Be(168);
    }

    [Fact]
    public void ParsesNativeIpv6Flow()
    {
        var flow = ConntrackParser.ParseLine(
            "ipv6    10 tcp      6 300 ESTABLISHED src=2001:db8::100 dst=2001:db8:ffff::1 sport=50000 dport=443 packets=10 bytes=1000 src=2001:db8:ffff::1 dst=2001:db8::100 sport=443 dport=50000 packets=8 bytes=8000 [ASSURED] mark=0 use=1");

        flow.Should().NotBeNull();
        flow!.OrigSrc.Should().Be(IPAddress.Parse("2001:db8::100"));
        flow.ReplyDst.Should().Be(IPAddress.Parse("2001:db8::100"));
    }

    [Fact]
    public void SkipsLineWithoutByteCounters()
    {
        // nf_conntrack_acct off: no packets/bytes fields.
        ConntrackParser.ParseLine(
            "ipv4     2 udp      17 29 src=192.168.1.50 dst=203.0.113.8 sport=5353 dport=53 src=203.0.113.8 dst=198.51.100.7 sport=53 dport=5353 mark=0 use=2")
            .Should().BeNull();
    }

    [Fact]
    public void SkipsUnrepliedWithoutReplyBytes()
    {
        // [UNREPLIED] lines still carry both tuples on UniFi kernels; a malformed line without
        // the reply tuple is dropped rather than guessed at.
        ConntrackParser.ParseLine(
            "ipv4     2 tcp      6 100 SYN_SENT src=192.168.1.100 dst=203.0.113.34 sport=1 dport=443 packets=1 bytes=60 mark=0")
            .Should().BeNull();
    }

    [Fact]
    public void DistinctTuplesGetDistinctKeys()
    {
        var a = ConntrackParser.ParseLine(
            "ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport=1000 dport=443 packets=1 bytes=100 src=203.0.113.34 dst=198.51.100.7 sport=443 dport=1000 packets=1 bytes=100 mark=0");
        var b = ConntrackParser.ParseLine(
            "ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport=1001 dport=443 packets=1 bytes=100 src=203.0.113.34 dst=198.51.100.7 sport=443 dport=1001 packets=1 bytes=100 mark=0");

        a!.Key.Should().NotBe(b!.Key);
    }
}

public class ConntrackAccountantTests
{
    private static ConntrackHostView GatewayView()
    {
        var view = new ConntrackHostView();
        // LAN on br0 (192.168.1.0/24), a second VLAN on br30, WAN address on eth4.
        view.AddHostAddress(IPAddress.Parse("192.168.1.1"), "br0");
        view.AddConnectedSubnet(IPAddress.Parse("192.168.1.0"), 24);
        view.AddHostAddress(IPAddress.Parse("192.168.30.1"), "br30");
        view.AddConnectedSubnet(IPAddress.Parse("192.168.30.0"), 24);
        view.AddHostAddress(IPAddress.Parse("198.51.100.7"), "eth4");
        view.AddNeighbor(IPAddress.Parse("192.168.1.100"), "aa:bb:cc:dd:ee:01");
        view.AddNeighbor(IPAddress.Parse("192.168.30.50"), "aa:bb:cc:dd:ee:02");
        return view;
    }

    private static ConntrackFlow NattedFlow(long origBytes, long replyBytes, int sport = 51512) =>
        ConntrackParser.ParseLine(
            $"ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport={sport} dport=443 packets=1 bytes={origBytes} src=203.0.113.34 dst=198.51.100.7 sport=443 dport={sport} packets=1 bytes={replyBytes} [ASSURED] mark=0")!;

    [Fact]
    public void FirstPassSeedsAndEmitsNothing()
    {
        var accountant = new ConntrackAccountant();
        accountant.Account(new[] { NattedFlow(1_000_000, 9_000_000) }, GatewayView()).Should().BeEmpty();
    }

    [Fact]
    public void SecondPassEmitsWindowDeltasByLanEndpoint()
    {
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { NattedFlow(1000, 5000) }, view);
        var deltas = accountant.Account(new[] { NattedFlow(1500, 25000) }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.Mac.Should().Be("aa:bb:cc:dd:ee:01");
        d.Ip.Should().Be("192.168.1.100");
        d.UpBytes.Should().Be(500);      // original tuple: from the client
        d.DownBytes.Should().Be(20000);  // reply tuple: toward the client
        d.WanIfName.Should().Be("eth4"); // the NAT address's interface
    }

    [Fact]
    public void InterVlanRoutedFlowIsNeverCounted()
    {
        // The exact mistake UniFi's own tallies make: gateway conntracks the routed flow, but
        // both real ends are site-local, so it is not WAN.
        var flow = ConntrackParser.ParseLine(
            "ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=192.168.30.50 sport=5000 dport=445 packets=1 bytes=1000 src=192.168.30.50 dst=192.168.1.100 sport=445 dport=5000 packets=1 bytes=1000 mark=0")!;
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { flow }, view);

        var second = ConntrackParser.ParseLine(
            "ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=192.168.30.50 sport=5000 dport=445 packets=9 bytes=900000 src=192.168.30.50 dst=192.168.1.100 sport=445 dport=5000 packets=9 bytes=900000 mark=0")!;
        accountant.Account(new[] { second }, view).Should().BeEmpty();
    }

    [Fact]
    public void InboundPortForwardAttributesToInternalServer()
    {
        // Remote is the ORIGINAL tuple's source; classifying by tuple order would invert it.
        // DNAT: original dst is the WAN address, reply src the internal server.
        string Line(long origBytes, long replyBytes) =>
            $"ipv4     2 tcp      6 100 ESTABLISHED src=203.0.113.99 dst=198.51.100.7 sport=40000 dport=8443 packets=1 bytes={origBytes} src=192.168.1.100 dst=203.0.113.99 sport=8443 dport=40000 packets=1 bytes={replyBytes} mark=0";
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ConntrackParser.ParseLine(Line(100, 100))! }, view);
        var deltas = accountant.Account(new[] { ConntrackParser.ParseLine(Line(10100, 600))! }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.Mac.Should().Be("aa:bb:cc:dd:ee:01");
        d.DownBytes.Should().Be(10000); // original tuple flows toward the server
        d.UpBytes.Should().Be(500);
        d.WanIfName.Should().Be("eth4"); // the pre-DNAT WAN address's interface
    }

    [Fact]
    public void GatewayOwnTrafficGetsAnIpOnlyRow()
    {
        string Line(long orig, long reply) =>
            $"ipv4     2 tcp      6 100 ESTABLISHED src=198.51.100.7 dst=203.0.113.50 sport=44000 dport=443 packets=1 bytes={orig} src=203.0.113.50 dst=198.51.100.7 sport=443 dport=44000 packets=1 bytes={reply} mark=0";
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ConntrackParser.ParseLine(Line(0, 0))! }, view);
        var deltas = accountant.Account(new[] { ConntrackParser.ParseLine(Line(700, 300))! }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.Ip.Should().Be("198.51.100.7"); // real WAN usage, attributed to the gateway itself
        d.Mac.Should().Be("");
        d.UpBytes.Should().Be(700);
        d.DownBytes.Should().Be(300);
    }

    [Fact]
    public void EndpointWithoutNeighborEntryGoesToUnattributedRemainder()
    {
        // An IPv6 privacy address already rotated out of the NDP table: never guessed to a client.
        string Line(long orig, long reply) =>
            $"ipv6    10 tcp      6 100 ESTABLISHED src=2001:db8:1::abcd dst=2001:db8:ffff::1 sport=50000 dport=443 packets=1 bytes={orig} src=2001:db8:ffff::1 dst=2001:db8:1::abcd sport=443 dport=50000 packets=1 bytes={reply} mark=0";
        var view = GatewayView();
        view.AddHostAddress(IPAddress.Parse("2001:db8:1::1"), "br0");
        view.AddConnectedSubnet(IPAddress.Parse("2001:db8:1::"), 64);
        var accountant = new ConntrackAccountant();
        accountant.Account(new[] { ConntrackParser.ParseLine(Line(0, 0))! }, view);
        var deltas = accountant.Account(new[] { ConntrackParser.ParseLine(Line(100, 200))! }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.Ip.Should().Be("");
        d.Mac.Should().Be("");
        d.DownBytes.Should().Be(200);
    }

    [Fact]
    public void CounterGoneBackwardSeedsAsNewFlowAndDeltasFromThere()
    {
        // A reused tuple whose counters restarted is seeded, not billed: its next delta counts.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { NattedFlow(1_000_000, 5_000_000) }, view);
        accountant.Account(new[] { NattedFlow(400, 900) }, view).Should().BeEmpty();
        var deltas = accountant.Account(new[] { NattedFlow(500, 1200) }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.UpBytes.Should().Be(100);
        d.DownBytes.Should().Be(300);
    }

    [Fact]
    public void UnseenTupleIsSeedOnlyNeverBilledItsTotal()
    {
        // The proc table is a seq-file read: an existing flow can be missed in one pass under
        // churn and reappear with its whole history. Billing that as one window inflated a
        // client by orders of magnitude (seen live: 164 GB "in 15 minutes" on a 1 Gbps WAN).
        // Seeding loses at most one window of a truly new flow - undercount, never inflation.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { NattedFlow(100, 100, sport: 1000) }, view);
        var deltas = accountant.Account(new[]
        {
            NattedFlow(100, 100, sport: 1000),
            NattedFlow(5_000_000_000, 60_000_000_000, sport: 2000),
        }, view);
        deltas.Should().BeEmpty(); // the reappeared/new tuple only seeds

        var next = accountant.Account(new[]
        {
            NattedFlow(100, 100, sport: 1000),
            NattedFlow(5_000_001_000, 60_000_002_000, sport: 2000),
        }, view);
        var d = next.Should().ContainSingle().Subject;
        d.UpBytes.Should().Be(1000);
        d.DownBytes.Should().Be(2000);
    }

    [Fact]
    public void DisappearedFlowJustStopsCounting()
    {
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { NattedFlow(100, 100) }, view);
        accountant.Account(System.Array.Empty<ConntrackFlow>(), view).Should().BeEmpty();
        // Its tuple coming back reads as a fresh seed, never as a resumed counter.
        accountant.Account(new[] { NattedFlow(50, 70) }, view).Should().BeEmpty();
        var deltas = accountant.Account(new[] { NattedFlow(90, 100) }, view);
        deltas.Should().ContainSingle().Subject.UpBytes.Should().Be(40);
    }

    [Fact]
    public void SamplesAggregatePerClientAndInterface()
    {
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { NattedFlow(0, 0, 1000), NattedFlow(0, 0, 2000) }, view);
        var deltas = accountant.Account(new[] { NattedFlow(10, 20, 1000), NattedFlow(30, 40, 2000) }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.UpBytes.Should().Be(40);
        d.DownBytes.Should().Be(60);
        d.Flows.Should().Be(2);
    }

    [Fact]
    public void MissedPassKeepsTheBaselineInsteadOfReseeding()
    {
        // A seq-file read can skip an existing flow for one pass. The entry is retained, so
        // the reappearance deltas from the old baseline instead of losing the growth to a
        // fresh seed.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { NattedFlow(100, 100) }, view);
        accountant.Account(System.Array.Empty<ConntrackFlow>(), view).Should().BeEmpty();
        var deltas = accountant.Account(new[] { NattedFlow(600, 1100) }, view);

        var d = deltas.Should().ContainSingle().Subject;
        d.UpBytes.Should().Be(500);
        d.DownBytes.Should().Be(1000);
    }
}

public class ConntrackDestroyReconcileTests
{
    private static ConntrackHostView GatewayView()
    {
        var view = new ConntrackHostView();
        view.AddHostAddress(IPAddress.Parse("192.168.1.1"), "br0");
        view.AddConnectedSubnet(IPAddress.Parse("192.168.1.0"), 24);
        view.AddHostAddress(IPAddress.Parse("198.51.100.7"), "eth4");
        view.AddNeighbor(IPAddress.Parse("192.168.1.100"), "aa:bb:cc:dd:ee:01");
        return view;
    }

    private static ConntrackFlow ProcFlow(long origBytes, long replyBytes, int sport = 51512) =>
        ConntrackParser.ParseLine(
            $"ipv4     2 tcp      6 100 ESTABLISHED src=192.168.1.100 dst=203.0.113.34 sport={sport} dport=443 packets=1 bytes={origBytes} src=203.0.113.34 dst=198.51.100.7 sport=443 dport={sport} packets=1 bytes={replyBytes} [ASSURED] mark=0")!;

    private static ConntrackFlow EventFlow(long origBytes, long replyBytes, int sport = 51512) =>
        ConntrackParser.ParseLine(
            $"[DESTROY] tcp      6 src=192.168.1.100 dst=203.0.113.34 sport={sport} dport=443 packets=99 bytes={origBytes} src=203.0.113.34 dst=198.51.100.7 sport=443 dport={sport} packets=99 bytes={replyBytes} [ASSURED] mark=0")!;

    [Fact]
    public void EventLineAndProcLineShareOneFlowKey()
    {
        // The reconcile lookup depends on it: `conntrack -E` has two bare lead tokens where
        // /proc has four, and both must key identically.
        EventFlow(1, 2).Key.Should().Be(ProcFlow(1, 2).Key);
    }

    [Fact]
    public void DestroyBillsTailPlusSeedForAFlowFirstSeenMidRun()
    {
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9) }, view);          // pass 1: unrelated
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9), ProcFlow(1000, 50_000) }, view); // first sight: seed
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9), ProcFlow(1500, 80_000) }, view); // billed 500/30k

        var r = accountant.AccountDestroy(EventFlow(2000, 100_000), view);
        r.Should().NotBeNull();
        r!.ReconUpBytes.Should().Be(2000 - 1500 + 1000);    // tail + the retained seed
        r.ReconDownBytes.Should().Be(100_000 - 80_000 + 50_000);
        r.DownBytes.Should().Be(0); // recon rides its own fields, never the live rate
        r.Mac.Should().Be("aa:bb:cc:dd:ee:01");
    }

    [Fact]
    public void DestroyOfAFirstSnapshotFlowBillsOnlyTheTail()
    {
        // A flow already present in the very first snapshot carries pre-coverage history: its
        // seed is never billed (DPI-sourced hours already hold those bytes), only its tail.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ProcFlow(1_000_000, 9_000_000) }, view);
        accountant.Account(new[] { ProcFlow(1_000_100, 9_000_200) }, view);

        var r = accountant.AccountDestroy(EventFlow(1_000_150, 9_000_500), view);
        r.Should().NotBeNull();
        r!.ReconUpBytes.Should().Be(50);
        r.ReconDownBytes.Should().Be(300);
    }

    [Fact]
    public void DestroyOfANeverSeenTupleBillsItsFullCounters()
    {
        // Born and dead entirely between passes: the event is authoritative for exactly one
        // connection and cannot fire twice, so full billing carries none of the seq-file-miss
        // ambiguity that forbids it in Account.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9) }, view);
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9) }, view);

        var r = accountant.AccountDestroy(EventFlow(700, 40_000, sport: 2000), view);
        r.Should().NotBeNull();
        r!.ReconUpBytes.Should().Be(700);
        r.ReconDownBytes.Should().Be(40_000);
    }

    [Fact]
    public void DestroyBelowTheTrackedBaselineIsSkippedAndTheEntryKept()
    {
        // A reused tuple's stray event: the entry tracks a newer flow whose counters are
        // already past the dead one's. Skip - undercount doctrine - and keep tracking.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9) }, view);
        accountant.Account(new[] { ProcFlow(5000, 5000) }, view);

        accountant.AccountDestroy(EventFlow(100, 100), view).Should().BeNull();
        var deltas = accountant.Account(new[] { ProcFlow(6000, 7000) }, view);
        deltas.Should().ContainSingle().Subject.UpBytes.Should().Be(1000);
    }

    [Fact]
    public void DestroyBeforeTheSecondPassBillsNothing()
    {
        // Until a second pass runs, nothing distinguishes pre-coverage flows from new ones.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.AccountDestroy(EventFlow(700, 40_000), view).Should().BeNull();
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9) }, view);
        accountant.AccountDestroy(EventFlow(700, 40_000), view).Should().BeNull();
    }

    [Fact]
    public void ReconciledFlowDoesNotDoubleBillOnALingeringSnapshot()
    {
        // The destroy removed the entry; if the same tuple somehow lingers in a later read it
        // reads as a fresh seed, never as a resumed counter.
        var accountant = new ConntrackAccountant();
        var view = GatewayView();
        accountant.Account(new[] { ProcFlow(0, 0, sport: 9) }, view);
        accountant.Account(new[] { ProcFlow(1000, 1000) }, view);
        accountant.AccountDestroy(EventFlow(1200, 1300), view).Should().NotBeNull();

        accountant.Account(new[] { ProcFlow(0, 0, sport: 9), ProcFlow(1200, 1300) }, view).Should().BeEmpty();
    }
}

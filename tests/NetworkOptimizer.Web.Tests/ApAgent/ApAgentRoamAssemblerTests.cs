using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Folding events into roams. One roam is reported by up to three access points - the gaining one
/// sees the association, the losing one the disassociation, and any peer is told over UBNT_ROAM
/// gossip - so the thing that has to hold is that a roam observed several times is still one row.
/// </summary>
public class ApAgentRoamAssemblerTests
{
    private const string ApOne = "aa:bb:cc:dd:ee:01";
    private const string ApTwo = "aa:bb:cc:dd:ee:02";
    private const string ApThree = "aa:bb:cc:dd:ee:03";
    private const string BssidOne = "aa:bb:cc:dd:ef:01";
    private const string BssidTwo = "aa:bb:cc:dd:ef:02";
    private const string Client = "00:11:22:33:44:55";

    private static readonly DateTime T0 = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentRoamAssembler Assembler()
    {
        var assembler = new ApAgentRoamAssembler();
        assembler.SetVaps(ApOne, [Vap("ath0", BssidOne, "5", 44)]);
        assembler.SetVaps(ApTwo, [Vap("ath1", BssidTwo, "6", 37)]);
        assembler.SetVaps(ApThree, []);
        return assembler;
    }

    private static ApAgentVap Vap(string name, string bssid, string band, int channel)
        => new() { Name = name, Bssid = bssid, Band = band, Channel = channel, Essid = "TestSsid" };

    private static ApRoamObservedEvent Assoc(string ap, string vap, string mac, double seconds, ulong seq = 1, bool gap = false)
        => Observed(ap, ApAgentEventTypes.Assoc, mac, seconds, seq, vap: vap, gap: gap);

    private static ApRoamObservedEvent Disassoc(string ap, string vap, string mac, double seconds, ulong seq = 1)
        => Observed(ap, ApAgentEventTypes.Disassoc, mac, seconds, seq, vap: vap);

    private static ApRoamObservedEvent Gossip(string ap, string type, string mac, string peerBssid, double seconds, ulong seq = 1)
        => Observed(ap, type, mac, seconds, seq, peerBssid: peerBssid);

    private static ApRoamObservedEvent Observed(
        string ap, string type, string mac, double seconds, ulong seq,
        string? vap = null, string? peerBssid = null, bool gap = false)
        => new(ap, new ApAgentEvent
        {
            Seq = seq,
            Type = type,
            Mac = mac,
            Vap = vap,
            PeerBssid = peerBssid,
            CollectedAt = T0.AddSeconds(seconds),
        }, gap);

    [Fact]
    public void A_first_association_is_a_join_not_a_roam()
    {
        var roams = Assembler().Process([Assoc(ApOne, "ath0", Client, 0)]);

        roams.Should().BeEmpty("there is no previous access point to have roamed from");
    }

    [Fact]
    public void Moving_between_access_points_records_both_ends_and_the_dwell()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([
            Disassoc(ApOne, "ath0", Client, 30, seq: 2),
            Assoc(ApTwo, "ath1", Client, 30.2, seq: 2),
        ]);

        roams.Should().HaveCount(1);
        var roam = roams[0];
        roam.ClientMac.Should().Be(Client);
        roam.FromApMac.Should().Be(ApOne);
        roam.FromBssid.Should().Be(BssidOne);
        roam.FromBand.Should().Be("5");
        roam.ToApMac.Should().Be(ApTwo);
        roam.ToBssid.Should().Be(BssidTwo);
        roam.Band.Should().Be("6");
        roam.Channel.Should().Be(37);
        roam.DwellSeconds.Should().BeApproximately(30.2, 0.01);
        roam.Source.Should().Be(RoamSources.Assoc);
    }

    [Fact]
    public void One_roam_seen_by_three_access_points_is_one_row()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([
            Disassoc(ApOne, "ath0", Client, 60, seq: 2),
            Assoc(ApTwo, "ath1", Client, 60.1, seq: 2),
            Gossip(ApOne, ApAgentEventTypes.RoamBroadcast, Client, BssidTwo, 60.4, seq: 3),
            Gossip(ApThree, ApAgentEventTypes.RoamToPeer, Client, BssidTwo, 60.9, seq: 1),
        ]);

        roams.Should().HaveCount(1, "the losing, gaining, and gossiping access points all describe the same landing");
        roams[0].Observers.Should().BeEquivalentTo([ApTwo, ApOne, ApThree]);
        roams[0].Source.Should().Be(RoamSources.Assoc, "the gaining access point saw it first-hand");
    }

    [Fact]
    public void Gossip_arriving_before_the_association_still_produces_one_row_and_prefers_the_association()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var first = assembler.Process([Gossip(ApThree, ApAgentEventTypes.RoamToPeer, Client, BssidTwo, 20, seq: 1)]);
        first.Should().HaveCount(1);
        first[0].Source.Should().Be(RoamSources.RoamToPeer);
        first[0].RecordId = 7;

        var second = assembler.Process([Assoc(ApTwo, "ath1", Client, 22, seq: 2)]);

        second.Should().HaveCount(1);
        second[0].RecordId.Should().Be(7, "a second observation updates the row it already wrote");
        second[0].Source.Should().Be(RoamSources.Assoc, "first-hand association outranks gossip");
        second[0].Observers.Should().BeEquivalentTo([ApThree, ApTwo]);
    }

    [Fact]
    public void A_roam_to_an_access_point_with_no_agent_is_still_recorded()
    {
        const string foreignBssid = "aa:bb:cc:dd:ef:09";
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([Gossip(ApOne, ApAgentEventTypes.RoamBroadcast, Client, foreignBssid, 15, seq: 2)]);

        roams.Should().HaveCount(1);
        roams[0].ToBssid.Should().Be(foreignBssid);
        roams[0].ToApMac.Should().BeNull("no access point of ours owns that BSSID");
    }

    [Fact]
    public void Two_separate_roams_outside_the_dedup_window_are_two_rows()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var first = assembler.Process([Assoc(ApTwo, "ath1", Client, 10, seq: 2)]);
        first.Should().HaveCount(1);
        first[0].RecordId = 1;

        var second = assembler.Process([Assoc(ApOne, "ath0", Client, 40, seq: 3)]);

        second.Should().HaveCount(1);
        second[0].RecordId.Should().BeNull("this is a different roam, not another view of the first");
    }

    [Fact]
    public void An_mlo_client_roams_on_its_mld_mac_not_its_link_mac()
    {
        const string linkMac = "00:11:22:33:44:aa";
        const string mldMac = "00:11:22:33:44:55";

        var assembler = Assembler();
        assembler.NoteClientKey(linkMac, mldMac);
        assembler.Process([Assoc(ApOne, "ath0", mldMac, 0)]);

        var roams = assembler.Process([Assoc(ApTwo, "ath1", linkMac, 5, seq: 2)]);

        roams.Should().HaveCount(1, "a link MAC that resolves to a known client is that client");
        roams[0].ClientMac.Should().Be(mldMac);
        roams[0].LinkMac.Should().Be(linkMac);
    }

    [Fact]
    public void A_client_that_left_hours_ago_and_came_back_elsewhere_is_not_a_roam()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([Assoc(ApTwo, "ath1", Client, ApAgentRoamAssembler.RoamMaxGap.TotalSeconds + 60, seq: 2)]);

        roams.Should().BeEmpty("that is a fresh visit, and calling it a roam invents a transition nobody made");
    }

    [Fact]
    public void A_rejoin_long_after_an_observed_disassociation_is_not_a_roam()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([
            Disassoc(ApOne, "ath0", Client, 10, seq: 2),
            Assoc(ApTwo, "ath1", Client, 10 + ApAgentRoamAssembler.RejoinGap.TotalSeconds + 30, seq: 3),
        ]);

        roams.Should().BeEmpty("the client was gone, so this is a new association rather than a transition");
    }

    [Fact]
    public void A_reassociation_to_the_same_bssid_is_not_a_roam()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([Assoc(ApOne, "ath0", Client, 20, seq: 2)]);

        roams.Should().BeEmpty("the client did not go anywhere");
    }

    [Fact]
    public void A_roam_read_out_of_a_truncated_window_is_flagged_rather_than_trusted()
    {
        var assembler = Assembler();
        assembler.Process([Assoc(ApOne, "ath0", Client, 0)]);

        var roams = assembler.Process([Assoc(ApTwo, "ath1", Client, 5, seq: 2, gap: true)]);

        roams.Should().HaveCount(1);
        roams[0].AfterEventGap.Should().BeTrue("events were lost, so the access point it left may be wrong");
    }
}

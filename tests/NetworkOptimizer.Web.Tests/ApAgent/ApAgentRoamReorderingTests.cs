using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentRoamReorderingTests
{
    private const string A = "aa:bb:cc:dd:ee:01";
    private const string B = "aa:bb:cc:dd:ee:02";
    private const string C = "aa:bb:cc:dd:ee:03";
    private const string Client = "00:11:22:33:44:55";
    private const string OtherClient = "00:11:22:33:44:66";
    private const string BssidB = "aa:bb:cc:dd:ef:02";
    private static readonly DateTime Start = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentRoamAssembler Create()
    {
        var a = new ApAgentRoamAssembler();
        a.SetVaps(A, [
            new() { Name = "a5", Bssid = "aa:bb:cc:dd:ef:01", Band = "5", Channel = 44 },
            new() { Name = "a2", Bssid = "aa:bb:cc:dd:ef:04", Band = "2.4", Channel = 1 }
        ]);
        a.SetVaps(B, [new() { Name = "b5", Bssid = BssidB, Band = "5", Channel = 112 }]);
        a.SetVaps(C, [new() { Name = "c5", Bssid = "aa:bb:cc:dd:ef:03", Band = "5", Channel = 149 }]);
        a.Process([Event(A, ApAgentEventTypes.Assoc, "a5", 0)]);
        return a;
    }

    private static ApRoamObservedEvent Event(string ap, string type, string vap, double at,
        string client = Client, string? peer = null, bool gap = false)
        => new(ap, new ApAgentEvent { Type = type, Vap = vap, Mac = client,
            CollectedAt = Start.AddSeconds(at), PeerBssid = peer }, gap);

    [Fact]
    public void A_long_dwell_with_arrival_timestamped_before_departure_is_recorded()
    {
        var a = Create();
        const double arrival = 2247.5677528;
        var rows = a.Process([
            Event(A, ApAgentEventTypes.Disassoc, "a5", arrival + .0021528),
            Event(B, ApAgentEventTypes.Assoc, "b5", arrival),
            Event(A, ApAgentEventTypes.RoamToPeer, "a5", arrival + .0298, peer: BssidB)
        ]);

        rows.Should().ContainSingle();
        rows[0].FromApMac.Should().Be(A);
        rows[0].ToApMac.Should().Be(B);
        rows[0].RoamedAt.Should().Be(Start.AddSeconds(arrival));
        rows[0].DwellSeconds.Should().BeApproximately(arrival, .000001);
        rows[0].Observers.Should().BeEquivalentTo([A, B]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reversed_timestamps_work_when_the_two_sides_are_read_in_separate_passes(bool arrivalFirst)
    {
        var a = Create();
        var arrive = Event(B, ApAgentEventTypes.Assoc, "b5", 3600);
        var leave = Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002);
        a.Process([arrivalFirst ? arrive : leave]).Should().BeEmpty();

        var rows = a.Process([arrivalFirst ? leave : arrive]);

        rows.Should().ContainSingle();
        rows[0].FromApMac.Should().Be(A);
        rows[0].ToApMac.Should().Be(B);
        rows[0].DwellSeconds.Should().Be(3600);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-1.001, false)]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(60.001, false)]
    public void Late_departures_respect_both_the_skew_and_forward_gap_limits(double gap, bool expected)
    {
        var a = Create();
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600)]).Should().BeEmpty();
        var rows = a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600 - gap)]);

        rows.Should().HaveCount(expected ? 1 : 0);
    }

    [Theory]
    [InlineData(C, "c5", Client)]
    [InlineData(A, "a2", Client)]
    [InlineData(A, "a5", OtherClient)]
    public void An_unrelated_departure_does_not_confirm_the_pending_move(string ap, string vap, string client)
    {
        var a = Create();
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600)]).Should().BeEmpty();
        a.Process([Event(ap, ApAgentEventTypes.Disassoc, vap, 3600.002, client)]).Should().BeEmpty();

        a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)])
            .Should().ContainSingle();
    }

    [Fact]
    public void Late_confirmation_preserves_a_quick_return_and_later_band_change()
    {
        var a = Create();
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600)]).Should().BeEmpty();
        var back = a.Process([Event(A, ApAgentEventTypes.Assoc, "a2", 3608.8)]);
        back.Should().ContainSingle();
        back[0].FromApMac.Should().Be(B);
        var band = a.Process([Event(A, ApAgentEventTypes.Assoc, "a5", 3610.5)]);
        band.Should().ContainSingle();
        band[0].FromApMac.Should().Be(A);
        band[0].ToApMac.Should().Be(A);

        var outward = a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)]);
        outward.Should().ContainSingle();
        outward[0].FromApMac.Should().Be(A);
        outward[0].ToApMac.Should().Be(B);
        outward[0].DwellSeconds.Should().Be(3600);

        var next = a.Process([Event(C, ApAgentEventTypes.Assoc, "c5", 3620)]);
        next.Should().ContainSingle();
        next[0].FromApMac.Should().Be(A, "confirming old evidence must not move the client back to B");
        next[0].FromBand.Should().Be("5");
        next[0].DwellSeconds.Should().Be(9.5);
    }

    [Fact]
    public void Pending_gossip_and_repeated_reports_update_one_confirmed_candidate()
    {
        var a = Create();
        a.Process([Event(C, ApAgentEventTypes.RoamToPeer, "c5", 3600, peer: BssidB)]).Should().BeEmpty();
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600.1)]).Should().BeEmpty();
        var rows = a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)]);
        rows.Should().ContainSingle();
        var row = rows[0];
        row.RecordId = 42;
        row.Source.Should().Be(RoamSources.Assoc);
        row.RoamedAt.Should().Be(Start.AddSeconds(3600));
        row.DwellSeconds.Should().Be(3600);
        row.Observers.Should().Contain([B, C]);

        a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)]).Should().BeEmpty();
        var duplicate = a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600.1)]);
        duplicate.Should().ContainSingle().Which.Should().BeSameAs(row);
        duplicate[0].RecordId.Should().Be(42);
    }

    [Fact]
    public void Pending_evidence_expires_before_a_much_later_confirmation_pass()
    {
        var a = Create();
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600)]).Should().BeEmpty();
        a.Process([Event(C, ApAgentEventTypes.Assoc, "c5", 3901, OtherClient)]);

        a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)]).Should().BeEmpty();
    }

    [Fact]
    public void Reordered_confirmation_keeps_event_gap_warning()
    {
        var a = Create();
        var rows = a.Process([
            Event(B, ApAgentEventTypes.Assoc, "b5", 3600, gap: true),
            Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)
        ]);

        rows.Should().ContainSingle().Which.AfterEventGap.Should().BeTrue();
    }

    [Fact]
    public void A_late_arrival_report_enriches_history_without_rewinding_current_membership()
    {
        var a = Create();
        var first = a.Process([
            Event(B, ApAgentEventTypes.Assoc, "b5", 3600),
            Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)
        ]);
        first.Should().ContainSingle();
        first[0].RecordId = 42;
        a.Process([Event(A, ApAgentEventTypes.Assoc, "a2", 3608.8)]);
        var late = a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600.1)]);
        late.Should().ContainSingle().Which.RecordId.Should().Be(42);

        var next = a.Process([Event(C, ApAgentEventTypes.Assoc, "c5", 3620)]);
        next.Should().ContainSingle().Which.FromApMac.Should().Be(A);
        next[0].FromBand.Should().Be("2.4");
        next[0].DwellSeconds.Should().BeApproximately(11.2, .000001);
    }

    [Fact]
    public void A_second_real_visit_within_the_dedup_window_is_separate_from_late_confirmation()
    {
        var a = Create();
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600)]).Should().BeEmpty();
        a.Process([Event(A, ApAgentEventTypes.Assoc, "a5", 3608.8)]).Should().ContainSingle();
        var second = a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3609)]);
        second.Should().ContainSingle();
        second[0].RecordId = 2;
        second[0].DwellSeconds.Should().BeApproximately(.2, .000001);

        var first = a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)]);
        first.Should().ContainSingle();
        first[0].RecordId = 1;
        first[0].DwellSeconds.Should().Be(3600);

        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3609.05)])
            .Should().ContainSingle().Which.RecordId.Should().Be(2);
        a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600.05)])
            .Should().ContainSingle().Which.RecordId.Should().Be(1);
    }

    [Fact]
    public void An_earlier_first_hand_landing_corrects_dwell_after_gossip_confirmation()
    {
        var a = Create();
        a.Process([Event(C, ApAgentEventTypes.RoamToPeer, "c5", 3600.003, peer: BssidB)]);
        var confirmed = a.Process([Event(A, ApAgentEventTypes.Disassoc, "a5", 3600.002)]);
        confirmed.Should().ContainSingle();
        confirmed[0].RecordId = 42;

        var enriched = a.Process([Event(B, ApAgentEventTypes.Assoc, "b5", 3600)]);
        enriched.Should().ContainSingle().Which.RecordId.Should().Be(42);
        enriched[0].RoamedAt.Should().Be(Start.AddSeconds(3600));
        enriched[0].DwellSeconds.Should().Be(3600);

        var back = a.Process([Event(A, ApAgentEventTypes.Assoc, "a5", 3608.8)]);
        back.Should().ContainSingle().Which.DwellSeconds.Should().BeApproximately(8.8, .000001);
    }
}

using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentClientEvidenceTests
{
    private const string Ap = "aa:bb:cc:dd:ee:01";
    private const string ClientMac = "cc:cc:cc:00:00:01";
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentWifiSample Sample(double? latency, long? stalls, int? join = -75, int assocSeconds = 7200) => new(
        ClientMac, Ap, "5ghz", 36, 80, -70, -95, 25, 400000, 300000, 90, 1000, 2000, null, false,
        null, null, null, latency, null, stalls, null, null, false, 1, 2, null,
        JoinSignal: join, AssocSeconds: assocSeconds, BtmRequests: 1, BtmAccepted: 0);

    [Fact]
    public void Facts_are_the_latest_sample_and_the_hour_is_a_median_and_a_delta()
    {
        var evidence = new ApAgentClientEvidence();
        evidence.Record(Sample(10, 100), T0);
        evidence.Record(Sample(80, 110), T0.AddMinutes(20));
        evidence.Record(Sample(30, 137), T0.AddMinutes(40));

        var facts = evidence.Latest(Ap, T0.AddMinutes(41)).Single();
        facts.JoinSignal.Should().Be(-75);
        facts.AssociatedFor.Should().Be(TimeSpan.FromHours(2));
        facts.RoamNudges.Should().Be(1);
        facts.RoamNudgesAccepted.Should().Be(0);
        facts.NegotiatedWidth.Should().Be(80);
        facts.Nss.Should().Be(2);

        var (median, stalls) = evidence.HourStats(Ap, ClientMac, T0.AddMinutes(41));
        median.Should().Be(30);
        stalls.Should().Be(37);
    }

    [Fact]
    public void Readings_older_than_an_hour_leave_the_ring()
    {
        var evidence = new ApAgentClientEvidence();
        evidence.Record(Sample(10, 0), T0);
        evidence.Record(Sample(50, 40), T0.AddMinutes(70));

        var (median, stalls) = evidence.HourStats(Ap, ClientMac, T0.AddMinutes(71));
        median.Should().Be(50);
        stalls.Should().Be(0, "one reading is no delta");
    }

    [Fact]
    public void A_counter_reset_counts_from_zero()
    {
        var evidence = new ApAgentClientEvidence();
        evidence.Record(Sample(10, 500), T0);
        evidence.Record(Sample(10, 3), T0.AddMinutes(10));

        evidence.HourStats(Ap, ClientMac, T0.AddMinutes(11)).Stalls.Should().Be(3);
    }

    [Fact]
    public void A_client_not_sampled_for_five_minutes_is_forgotten()
    {
        var evidence = new ApAgentClientEvidence();
        evidence.Record(Sample(10, 0), T0);

        evidence.Latest(Ap, T0.AddMinutes(6)).Should().BeEmpty();
        evidence.Prune(T0.AddMinutes(6));
        evidence.HourStats(Ap, ClientMac, T0.AddMinutes(6)).Should().Be(((double?)null, (int?)null));
    }

    [Fact]
    public void The_enricher_copies_facts_onto_covered_clients_only()
    {
        var evidence = new ApAgentClientEvidence();
        evidence.Record(Sample(60, 5), T0);
        evidence.Record(Sample(60, 30), T0.AddMinutes(1));

        var coveredAp = new AccessPointSnapshot { Mac = Ap, Name = "AP-1", TotalClients = 9, Status = new(DeviceStatusKind.Online, "Online") };
        var otherAp = new AccessPointSnapshot { Mac = "aa:bb:cc:dd:ee:02", Name = "AP-2", TotalClients = 4 };
        var covered = new WirelessClientSnapshot { Mac = ClientMac.ToUpperInvariant(), ApMac = Ap, Band = RadioBand.Band5GHz, Signal = -70 };
        var uncovered = new WirelessClientSnapshot { Mac = "cc:cc:cc:00:00:02", ApMac = otherAp.Mac, Band = RadioBand.Band5GHz, Signal = -60 };

        var enriched = ApAgentClientEnricher.Apply(
            [coveredAp, otherAp], [covered, uncovered],
            ap => evidence.Latest(ap, T0.AddMinutes(2)),
            (ap, c) => evidence.HourStats(ap, c, T0.AddMinutes(2)),
            ap => ap == Ap ? 7 : null,
            T0.AddMinutes(2));

        enriched.Should().Be(1);
        covered.JoinSignal.Should().Be(-75);
        covered.AssociatedFor.Should().Be(TimeSpan.FromHours(2));
        covered.NegotiatedWidth.Should().Be(80);
        covered.MeasuredLatencyAvgMs.Should().Be(60);
        covered.MeasuredTcpStalls.Should().Be(25);
        coveredAp.MeasuredClientCount.Should().Be(7);
        coveredAp.EffectiveClientCount.Should().Be(7);

        uncovered.JoinSignal.Should().BeNull();
        uncovered.MeasuredLatencyAvgMs.Should().BeNull();
        otherAp.MeasuredClientCount.Should().BeNull();
        otherAp.EffectiveClientCount.Should().Be(4);
    }

    [Fact]
    public void Stale_facts_are_not_copied()
    {
        var evidence = new ApAgentClientEvidence();
        evidence.Record(Sample(60, 5), T0);
        var client = new WirelessClientSnapshot { Mac = ClientMac, ApMac = Ap, Band = RadioBand.Band5GHz, Signal = -70 };

        var enriched = ApAgentClientEnricher.Apply(
            [new AccessPointSnapshot { Mac = Ap }], [client],
            ap => evidence.Latest(ap, T0.AddMinutes(3)),
            (ap, c) => evidence.HourStats(ap, c, T0.AddMinutes(3)),
            _ => null,
            T0.AddMinutes(3));

        enriched.Should().Be(0);
        client.JoinSignal.Should().BeNull();
    }
}

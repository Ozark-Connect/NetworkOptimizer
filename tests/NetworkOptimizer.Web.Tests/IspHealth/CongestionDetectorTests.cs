using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class CongestionDetectorTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [Fact]
    public void Detects_sustained_rtt_and_jitter_elevation()
    {
        var humpStart = TestSeries.Start.AddHours(12);
        var humpEnd = humpStart.AddMinutes(45);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(humpStart, humpEnd, rttMs: 25, jitterMs: 5);

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(1);
        events[0].Start.Should().Be(humpStart);
        events[0].End.Should().Be(humpEnd);
        events[0].PeakRttMs.Should().Be(25);
        events[0].BaselineRttMs.Should().Be(5);
        events[0].IsShared.Should().BeFalse();
    }

    [Fact]
    public void Rtt_elevation_without_jitter_elevation_is_not_congestion()
    {
        var humpStart = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(humpStart, humpStart.AddMinutes(45), rttMs: 25, jitterMs: 0.5);

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Short_hump_below_minimum_duration_is_ignored()
    {
        var humpStart = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(humpStart, humpStart.AddMinutes(15), rttMs: 25, jitterMs: 5);

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Sustained_event_with_a_dip_and_milder_tail_stays_one_event()
    {
        // A peak, a brief dip, then a milder-but-still-jittery tail - one event spanning the whole
        // span, not truncated to the peak. Mirrors a real multi-hour event whose second half stayed
        // elevated (jitter still up) just under the strict entry gate.
        var start = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(start, start.AddMinutes(45), rttMs: 9, jitterMs: 3)                  // peak: clears entry gate
            .WithSegment(start.AddMinutes(45), start.AddMinutes(60), rttMs: 6.1, jitterMs: 1.0)  // dip: below entry, jitter sustains
            .WithSegment(start.AddMinutes(60), start.AddMinutes(135), rttMs: 6.5, jitterMs: 1.0); // milder tail: jitter sustains

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(1);
        events[0].Start.Should().Be(start);
        events[0].End.Should().Be(start.AddMinutes(135));
    }

    [Fact]
    public void Mild_elevation_that_never_meets_the_entry_gate_is_not_congestion()
    {
        // An hour at the sustain level but never the strict entry gate (no peak). The hysteresis gate
        // continues an active run; it must never start one, so nothing is flagged here.
        var start = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(start, start.AddMinutes(60), rttMs: 6.5, jitterMs: 1.0);

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Path_shift_plateau_with_baseline_jitter_does_not_become_congestion()
    {
        // A transit path change: RTT steps up to a flat plateau and back, jitter at baseline the
        // whole time (a brief blip at the transition can fire the entry gate). With jitter flat there
        // is no congestion signature to sustain, so the run collapses to the sub-30-min transition
        // and nothing is flagged. The step detector owns this shape, not congestion.
        var start = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 16, jitterMs: 0.5)
            .WithSegment(start, start.AddMinutes(15), rttMs: 24, jitterMs: 3)            // transition turbulence
            .WithSegment(start.AddMinutes(15), start.AddHours(3), rttMs: 24, jitterMs: 0.5); // flat plateau, baseline jitter

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Simultaneous_events_across_asns_merge_into_shared_event()
    {
        var humpStart = TestSeries.Start.AddHours(19);
        var humpEnd = humpStart.AddHours(2);
        var seriesA = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(humpStart, humpEnd, rttMs: 30, jitterMs: 6);
        var seriesB = TestSeries.Flat(TestSeries.Start, Day, rttMs: 12, jitterMs: 0.8)
            .WithSegment(humpStart, humpEnd, rttMs: 45, jitterMs: 8);

        var events = CongestionDetector.Detect(new[]
        {
            TestSeries.Asn(64500, "TransitOne", seriesA),
            TestSeries.Asn(64501, "TransitTwo", seriesB)
        }, Options);

        events.Should().HaveCount(1);
        events[0].IsShared.Should().BeTrue();
        events[0].AsnNumbers.Should().BeEquivalentTo(new[] { 64500, 64501 });
        events[0].Start.Should().Be(humpStart);
        events[0].End.Should().Be(humpEnd);
    }

    [Fact]
    public void Non_overlapping_events_stay_separate()
    {
        var seriesA = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(TestSeries.Start.AddHours(6), TestSeries.Start.AddHours(7), rttMs: 30, jitterMs: 6);
        var seriesB = TestSeries.Flat(TestSeries.Start, Day, rttMs: 12, jitterMs: 0.8)
            .WithSegment(TestSeries.Start.AddHours(18), TestSeries.Start.AddHours(19), rttMs: 45, jitterMs: 8);

        var events = CongestionDetector.Detect(new[]
        {
            TestSeries.Asn(64500, "TransitOne", seriesA),
            TestSeries.Asn(64501, "TransitTwo", seriesB)
        }, Options);

        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => !e.IsShared);
    }

    [Fact]
    public void Tolerates_single_bucket_gap_within_event()
    {
        var humpStart = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 5, jitterMs: 0.5)
            .WithSegment(humpStart, humpStart.AddMinutes(30), rttMs: 25, jitterMs: 5)
            .WithSegment(humpStart.AddMinutes(45), humpStart.AddMinutes(75), rttMs: 25, jitterMs: 5);

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(1);
        events[0].Start.Should().Be(humpStart);
        events[0].End.Should().Be(humpStart.AddMinutes(75));
    }
}

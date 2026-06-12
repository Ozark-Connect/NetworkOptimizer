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

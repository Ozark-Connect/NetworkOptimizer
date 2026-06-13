using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class StepChangeDetectorTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [Fact]
    public void Detects_persistent_upward_step()
    {
        var stepAt = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 10, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 20, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(1);
        events[0].Direction.Should().Be(PathShiftDirection.Up);
        events[0].BeforeMedianMs.Should().Be(10);
        events[0].AfterMedianMs.Should().Be(20);
        events[0].Time.Should().Be(stepAt);
    }

    [Fact]
    public void Detects_persistent_downward_step()
    {
        var stepAt = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 20, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 10, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(1);
        events[0].Direction.Should().Be(PathShiftDirection.Down);
        events[0].DeltaMs.Should().Be(-10);
    }

    [Fact]
    public void Dip_and_return_reports_two_events()
    {
        var dipStart = TestSeries.Start.AddHours(8);
        var dipEnd = TestSeries.Start.AddHours(16);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 20, jitterMs: 0.5)
            .WithSegment(dipStart, dipEnd, rttMs: 10, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(2);
        events[0].Direction.Should().Be(PathShiftDirection.Down);
        events[1].Direction.Should().Be(PathShiftDirection.Up);
    }

    [Fact]
    public void Reverting_hump_is_not_a_step()
    {
        var humpStart = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 10, jitterMs: 0.5)
            .WithSegment(humpStart, humpStart.AddMinutes(30), rttMs: 25, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Sub_threshold_change_is_ignored()
    {
        var stepAt = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 10, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 11, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Two_ms_step_on_low_rtt_path_is_detected()
    {
        // Real transit shifts run 2-3 ms on ~10 ms paths; the thresholds were tuned to catch them
        var stepAt = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 10, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 12, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().ContainSingle().Which.DeltaMs.Should().Be(2);
    }

    [Fact]
    public void Overlapping_iqrs_suppress_noisy_candidate()
    {
        var samples = new List<LatencySample>();
        var stepAt = TestSeries.Start.AddHours(12);
        for (var t = TestSeries.Start; t < TestSeries.Start + Day; t = t.AddMinutes(1))
        {
            var alternating = t.Minute % 2 == 0;
            var beforeStep = t < stepAt;
            var rtt = beforeStep ? (alternating ? 5.0 : 15.0) : (alternating ? 10.0 : 20.0);
            samples.Add(new LatencySample(t, rtt, rtt + 0.5, 0.5, 0));
        }

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Correlated_steps_across_targets_merge_into_one_event()
    {
        var stepAt = TestSeries.Start.AddHours(12);
        var seriesA = TestSeries.Flat(TestSeries.Start, Day, rttMs: 10, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 20, jitterMs: 0.5);
        var seriesB = TestSeries.Flat(TestSeries.Start, Day, rttMs: 30, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 45, jitterMs: 0.5);

        var events = StepChangeDetector.Detect(new[]
        {
            TestSeries.Asn(64500, "TransitOne", seriesA),
            TestSeries.Asn(64501, "TransitTwo", seriesB)
        }, Options);

        // Both paths step up at the same boundary: one routing event, two paths.
        events.Should().ContainSingle();
        events[0].CorrelatedTargetCount.Should().Be(2);
        // Representative is the nearest hop (lowest before-level).
        events[0].BeforeMedianMs.Should().BeApproximately(10, 0.5);
    }
}

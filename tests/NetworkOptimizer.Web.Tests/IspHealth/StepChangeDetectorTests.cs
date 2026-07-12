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
    public void Short_lived_shift_reports_both_up_and_down()
    {
        var shiftUp = TestSeries.Start.AddHours(10);
        var shiftDown = shiftUp.AddMinutes(90);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 24, jitterMs: 0.3)
            .WithSegment(shiftUp, shiftDown, rttMs: 25.5, jitterMs: 0.3);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(2);
        events[0].Direction.Should().Be(PathShiftDirection.Up);
        events[1].Direction.Should().Be(PathShiftDirection.Down);
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
    public void Revert_reports_elevated_to_reverted_magnitude_not_transition_window()
    {
        // Production shape: a path steps up and holds for hours, then reverts. The drop
        // lands mid-window, so the transition window (wide IQR) fails the stability gate
        // and is skipped; the window just before the first settled reverted window then
        // sits at the reverted level. Reporting windows[r-1] -> windows[r] yielded a
        // "0 ms" delta for a real ~12 ms revert. The revert must report the elevated
        // level it actually came down from, not the transition-adjacent window.
        var stepUp = TestSeries.Start.AddHours(10);
        var revertAt = TestSeries.Start.AddHours(22).AddMinutes(7); // mid 22:00-22:30 window
        var span = TimeSpan.FromHours(34);
        var samples = TestSeries.Flat(TestSeries.Start, span, rttMs: 10, jitterMs: 0.5)
            .WithSegment(stepUp, TestSeries.Start.AddHours(22), rttMs: 23, jitterMs: 0.5)
            .WithSegment(revertAt, TestSeries.Start + span, rttMs: 11, jitterMs: 0.5);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().HaveCount(2);
        events[0].Direction.Should().Be(PathShiftDirection.Up);
        var revert = events[1];
        revert.Direction.Should().Be(PathShiftDirection.Down);
        revert.BeforeMedianMs.Should().BeApproximately(23, 1.0);
        revert.AfterMedianMs.Should().BeApproximately(11, 1.0);
        Math.Abs(revert.DeltaMs).Should().BeGreaterThan(10);
    }

    [Fact]
    public void Mid_window_shift_with_transition_median_outside_band_is_detected()
    {
        // Production shape (Cloudflare path, Jul 2026): the shift landed mid-window and a
        // small dip just before it left the transition window's median slightly BELOW the
        // old level. Requiring the transition median to sit strictly between the levels
        // rejected the step-up entirely, while the matching step-down was only caught
        // because its transition median landed 0.01 ms inside the band.
        var dipStart = TestSeries.Start.AddHours(12);
        var shiftAt = dipStart.AddMinutes(20);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 13.3, jitterMs: 0.3)
            .WithSegment(dipStart, shiftAt, rttMs: 12.9, jitterMs: 0.3)
            .WithSegment(shiftAt, TestSeries.Start + Day, rttMs: 17.5, jitterMs: 0.3);

        var events = StepChangeDetector.DetectForSeries(TestSeries.Asn(64500, "TransitOne", samples), Options);

        events.Should().ContainSingle();
        events[0].Direction.Should().Be(PathShiftDirection.Up);
        events[0].BeforeMedianMs.Should().BeApproximately(13.3, 0.1);
        events[0].AfterMedianMs.Should().BeApproximately(17.5, 0.1);
        events[0].Time.Should().BeCloseTo(shiftAt, TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Stable_plateau_outside_the_band_does_not_bridge_a_step()
    {
        // A full window resting at its own level outside the two step levels is a distinct
        // plateau, not a transition - the skip-one-window comparison must still reject it.
        var plateauStart = TestSeries.Start.AddHours(12);
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 20, jitterMs: 0.3)
            .WithSegment(plateauStart, plateauStart.AddMinutes(30), rttMs: 10, jitterMs: 0.3)
            .WithSegment(plateauStart.AddMinutes(30), TestSeries.Start + Day, rttMs: 17, jitterMs: 0.3);

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

    [Fact]
    public void Correlated_shift_is_labeled_from_an_on_path_hop_not_a_nearer_destination()
    {
        // A transit hop (higher RTT) and an internet/CDN destination (lower RTT) step at the
        // same boundary. The label must come from the on-path transit hop, not the nearer
        // destination - even though the destination has the lower before-level.
        var stepAt = TestSeries.Start.AddHours(12);
        var transitSamples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 20, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 30, jitterMs: 0.5);
        var destSamples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 10, jitterMs: 0.5)
            .WithSegment(stepAt, TestSeries.Start + Day, rttMs: 20, jitterMs: 0.5);

        var transit = new AsnSeries { AsnNumber = 3356, AsnName = "Level 3", TargetIds = { "custom-abc" }, Samples = transitSamples };
        var destination = new AsnSeries { AsnNumber = 19281, AsnName = "Quad9 DFW", TargetIds = { "path-quad9" }, Samples = destSamples, IsDestination = true };

        var events = StepChangeDetector.Detect(new[] { transit, destination }, Options);

        events.Should().ContainSingle();
        events[0].CorrelatedTargetCount.Should().Be(2);
        events[0].AsnName.Should().Be("Level 3");
    }
}

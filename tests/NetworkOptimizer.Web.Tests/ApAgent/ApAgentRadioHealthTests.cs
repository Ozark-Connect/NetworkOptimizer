using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The CCA wedge and the counters behind it. The measured fault is Rx Clear approaching Cycle with
/// a Tx Frame delta of zero, and healthy idle is Rx Clear moving with Tx Frame because the only
/// thing making the channel busy is our own beacons. The counters are unsigned 32-bit and wrap, and
/// 0xFFFFFFFF is a "no reading" sentinel, so both have to survive differencing.
/// </summary>
public class ApAgentRadioHealthTests
{
    private const string Radio = "wifi0";
    private static readonly DateTime T0 = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentRadioAirtime Reading(
        long cycle, long rxClear, long txFrame, long pdevResets, double seconds, string radio = Radio, string band = "6")
        => new(
            radio,
            band,
            37,
            160,
            -96,
            new Dictionary<string, long>
            {
                [ApAgentRadioCounters.Cycle] = cycle,
                [ApAgentRadioCounters.RxClear] = rxClear,
                [ApAgentRadioCounters.TxFrame] = txFrame,
                [ApAgentRadioCounters.PhyErr] = 0,
                [ApAgentRadioCounters.PdevResets] = pdevResets,
            },
            new Dictionary<string, long>(),
            30,
            T0.AddSeconds(seconds));

    [Fact]
    public void An_absent_counter_has_no_reading()
        => ApAgentRadioCounters.Read(new Dictionary<string, long>(), ApAgentRadioCounters.Cycle).Should().BeNull();

    [Fact]
    public void The_sentinel_is_a_missing_reading_not_four_billion()
        => ApAgentRadioCounters
            .Read(new Dictionary<string, long> { [ApAgentRadioCounters.Cycle] = ApAgentRadioCounters.Sentinel },
                ApAgentRadioCounters.Cycle)
            .Should().BeNull();

    [Fact]
    public void A_normal_advance_is_the_difference()
        => ApAgentRadioCounters.Delta(1_000, 1_750).Should().Be(750);

    [Fact]
    public void A_counter_that_wrapped_past_the_top_of_the_range_gives_the_real_movement()
        => ApAgentRadioCounters.Delta(ApAgentRadioCounters.Modulus - 100, 400).Should().Be(500);

    [Fact]
    public void A_counter_that_was_reset_gives_no_delta_rather_than_a_guess()
        => ApAgentRadioCounters.Delta(2_000_000, 12).Should().BeNull("a reset is not a wrap, and inventing a delta is worse than none");

    [Fact]
    public void A_missing_reading_on_either_side_gives_no_delta()
    {
        ApAgentRadioCounters.Delta(null, 500).Should().BeNull();
        ApAgentRadioCounters.Delta(500, null).Should().BeNull();
    }

    [Fact]
    public void The_first_reading_only_seeds_the_tracker()
    {
        var tracker = new ApAgentRadioHealthTracker();

        tracker.Observe([Reading(1_000, 100, 100, 0, 0)]).Should().BeEmpty("there is nothing to difference it against");
    }

    [Fact]
    public void Healthy_idle_moves_rx_clear_with_tx_frame_and_does_not_read_as_a_wedge()
    {
        var tracker = new ApAgentRadioHealthTracker();
        tracker.Observe([Reading(1_000_000_000, 20_000_000, 20_000_000, 0, 0)]);

        // Only our own beacons make the channel busy, so both counters move together and both stay
        // far below cycle.
        var windows = tracker.Observe([Reading(1_030_000_000, 20_600_000, 20_600_000, 0, 30)]);

        windows.Should().HaveCount(1);
        windows[0].RxClearDelta.Should().Be(600_000);
        windows[0].TxFrameDelta.Should().Be(600_000);
        windows[0].BusyRatio.Should().BeApproximately(0.02, 0.001);
        windows[0].Wedged.Should().BeFalse();
    }

    [Fact]
    public void The_measured_wedge_signature_is_detected()
    {
        var tracker = new ApAgentRadioHealthTracker();
        tracker.Observe([Reading(1_000_000_000, 20_000_000, 20_000_000, 0, 0)]);

        // Rx Clear tracks Cycle almost exactly while Tx Frame has stopped moving at all.
        var windows = tracker.Observe([Reading(1_030_000_000, 49_900_000, 20_000_000, 0, 30)]);

        windows.Should().HaveCount(1);
        windows[0].TxFrameDelta.Should().Be(0);
        windows[0].Wedged.Should().BeTrue();
    }

    [Fact]
    public void A_silent_radio_that_still_sees_a_clear_channel_is_not_wedged()
    {
        // Tx Frame is flat, but the medium is reported clear, which is a radio with nothing to say.
        ApAgentRadioWedgeDetector.MatchesSignature(cycleDelta: 30_000_000, rxClearDelta: 300_000, txFrameDelta: 0)
            .Should().BeFalse();
    }

    [Fact]
    public void A_busy_channel_that_is_still_transmitting_is_not_wedged()
    {
        ApAgentRadioWedgeDetector.MatchesSignature(cycleDelta: 30_000_000, rxClearDelta: 29_900_000, txFrameDelta: 4_000_000)
            .Should().BeFalse("a congested channel is not a wedged radio");
    }

    [Fact]
    public void A_window_with_no_cycle_reading_is_not_wedged()
        => ApAgentRadioWedgeDetector.MatchesSignature(null, 29_900_000, 0).Should().BeFalse();

    [Fact]
    public void One_window_is_a_reading_and_two_are_a_condition()
    {
        var detector = new ApAgentRadioWedgeDetector();

        detector.Observe(Radio, wedged: true).Should().BeFalse("one window is not yet a condition");
        detector.Observe(Radio, wedged: true).Should().BeTrue();
        detector.Observe(Radio, wedged: true).Should().BeFalse("the episode has already been raised");
    }

    [Fact]
    public void A_radio_that_recovers_re_arms_the_alert()
    {
        var detector = new ApAgentRadioWedgeDetector();
        detector.Observe(Radio, wedged: true);
        detector.Observe(Radio, wedged: true).Should().BeTrue();

        detector.Observe(Radio, wedged: false);

        detector.Observe(Radio, wedged: true).Should().BeFalse();
        detector.Observe(Radio, wedged: true).Should().BeTrue("a second episode is a second alert");
    }

    [Fact]
    public void A_radio_resetting_at_its_normal_rate_is_not_reported()
    {
        // The case that made this rule noise: on U7 hardware 6 GHz resets continuously while its
        // siblings sit at zero for their whole uptime, so steady background resets must stay quiet.
        var tracker = new ApAgentRadioHealthTracker();
        tracker.Observe([
            Reading(1_000_000_000, 20_000_000, 20_000_000, 4, 0, radio: "wifi0", band: "6"),
            Reading(1_000_000_000, 20_000_000, 20_000_000, 0, 0, radio: "wifi1", band: "5"),
        ]);

        var windows = tracker.Observe([
            Reading(1_030_000_000, 20_600_000, 20_600_000, 7, 30, radio: "wifi0", band: "6"),
            Reading(1_030_000_000, 20_600_000, 20_600_000, 0, 30, radio: "wifi1", band: "5"),
        ]);

        // 3 resets in 30s is 6/min, which is the documented healthy residual.
        var baseline = new Dictionary<string, double> { ["wifi0"] = 6.0, ["wifi1"] = 0.0 };
        ApAgentRadioWedgeDetector.ElevatedResets(windows, baseline).Should().BeEmpty();
    }

    [Fact]
    public void A_reset_rate_far_above_its_own_baseline_is_reported()
    {
        var tracker = new ApAgentRadioHealthTracker();
        tracker.Observe([Reading(1_000_000_000, 20_000_000, 20_000_000, 100, 0, radio: "wifi0", band: "6")]);

        // 48 resets in 30s is 96/min, the rate measured during the real wedge.
        var windows = tracker.Observe([Reading(1_030_000_000, 20_600_000, 20_600_000, 148, 30, radio: "wifi0", band: "6")]);

        var baseline = new Dictionary<string, double> { ["wifi0"] = 6.0 };
        var elevated = ApAgentRadioWedgeDetector.ElevatedResets(windows, baseline);

        elevated.Should().HaveCount(1);
        elevated[0].Radio.Should().Be("wifi0");
    }

    [Fact]
    public void A_radio_with_no_baseline_yet_cannot_alert()
    {
        var tracker = new ApAgentRadioHealthTracker();
        tracker.Observe([Reading(1_000_000_000, 20_000_000, 20_000_000, 100, 0, radio: "wifi0", band: "6")]);
        var windows = tracker.Observe([Reading(1_030_000_000, 20_600_000, 20_600_000, 148, 30, radio: "wifi0", band: "6")]);

        // Nothing to compare against, so a first observation must not fire on its own history.
        ApAgentRadioWedgeDetector.ElevatedResets(windows, new Dictionary<string, double>()).Should().BeEmpty();
    }

        [Fact]
    public void A_wrapped_cycle_counter_does_not_fabricate_a_wedge()
    {
        var tracker = new ApAgentRadioHealthTracker();
        tracker.Observe([Reading(ApAgentRadioCounters.Modulus - 1_000, ApAgentRadioCounters.Modulus - 900_000, 500_000, 0, 0)]);

        var windows = tracker.Observe([Reading(29_000, 600_000, 1_100_000, 0, 30)]);

        windows.Should().HaveCount(1);
        windows[0].CycleDelta.Should().Be(30_000);
        windows[0].RxClearDelta.Should().Be(1_500_000);
        windows[0].TxFrameDelta.Should().Be(600_000);
        windows[0].Wedged.Should().BeFalse("the radio is transmitting, whatever the counters did on the way round");
    }
}

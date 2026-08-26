using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The direction pairing here is load-bearing and was confirmed with TJ: download is From Device and
/// is bounded by the access point's RX rate; upload is To Device and is bounded by its TX rate.
/// Getting it backwards would silently pick the wrong access point rather than fail.
/// </summary>
public class SpeedTestWifiFitTests
{
    private static WifiFitCandidate Ap(string mac, long txKbps, long rxKbps, int points = 5)
        => new(mac, "5ghz", txKbps, rxKbps, -60, points);

    /// <summary>
    /// The measured case: a meshed access point's uplink PHY (1921/1441 Mbps) against 10.9/21.3 Mbps
    /// of real throughput is 0.6% efficiency. The real association was roughly 59/8.
    /// </summary>
    [Fact]
    public void The_mesh_uplink_loses_to_the_real_association()
    {
        var mesh = Ap("mesh", txKbps: 1_921_000, rxKbps: 1_441_000);
        var real = Ap("real", txKbps: 144_000, rxKbps: 65_000);

        var best = SpeedTestWifiFit.Best(new[] { mesh, real }, fromDeviceBps: 14.0e6, toDeviceBps: 47.3e6);

        Assert.NotNull(best);
        Assert.Equal("real", best!.Candidate.ApMac);
    }

    /// <summary>
    /// Efficiency no longer disqualifies. Points are stamped when the access point read them, so an
    /// association present in the window is evidence; an implausible ratio means the PHY sample is
    /// unrepresentative, not that this is the wrong access point.
    /// </summary>
    [Fact]
    public void A_lone_candidate_survives_an_implausible_ratio()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { Ap("only", txKbps: 1_921_000, rxKbps: 1_441_000) },
            fromDeviceBps: 10.9e6, toDeviceBps: 21.3e6);

        Assert.True(scored[0].IsViable);
        Assert.True(scored[0].Score < 0.05, "the ratio is still reported, it just does not disqualify");
    }

    /// <summary>
    /// A PHY under the measured throughput is a sampling artifact - the rate dipped between reads -
    /// and the max over the window is what absorbs it. It must not throw the association away.
    /// </summary>
    [Fact]
    public void A_phy_under_the_throughput_is_a_dip_not_a_disqualification()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { Ap("dipped", txKbps: 10_000, rxKbps: 10_000) },
            fromDeviceBps: 400e6, toDeviceBps: 400e6);

        Assert.True(scored[0].IsViable);
        Assert.True(scored[0].FromDeviceEfficiency > 1);
    }

    /// <summary>Download pairs with RX. Swapping the pairing makes this candidate look impossible.</summary>
    [Fact]
    public void Download_is_bounded_by_rx_and_upload_by_tx()
    {
        // Download 90 Mbps needs RX >= 90; upload 20 Mbps needs TX >= 20. Correct pairing fits.
        var candidate = Ap("ap", txKbps: 30_000, rxKbps: 120_000);

        var scored = SpeedTestWifiFit.Score(new[] { candidate }, fromDeviceBps: 90e6, toDeviceBps: 20e6);

        Assert.True(scored[0].IsViable);
        Assert.Equal(0.75, scored[0].FromDeviceEfficiency!.Value, 2);
        Assert.Equal(0.667, scored[0].ToDeviceEfficiency!.Value, 2);
    }

    /// <summary>
    /// The direction guard: swap the rates and the same measurements produce an impossible ratio.
    /// If this ever reads under 1, the pairing has been inverted somewhere.
    /// </summary>
    [Fact]
    public void The_reverse_pairing_produces_an_impossible_ratio()
    {
        var swapped = Ap("ap", txKbps: 120_000, rxKbps: 30_000);

        var scored = SpeedTestWifiFit.Score(new[] { swapped }, fromDeviceBps: 90e6, toDeviceBps: 20e6);

        Assert.True(scored[0].FromDeviceEfficiency > 1, "90Mbps cannot cross a 30Mbps RX");
    }

    /// <summary>A distant or interfered link genuinely runs at low efficiency and must survive.</summary>
    [Fact]
    public void A_poor_but_real_link_is_kept()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { Ap("far", txKbps: 144_000, rxKbps: 144_000) },
            fromDeviceBps: 8e6, toDeviceBps: 8e6);

        Assert.True(scored[0].IsViable);
    }

    [Fact]
    public void A_single_direction_test_scores_on_the_direction_it_has()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { Ap("ap", txKbps: 200_000, rxKbps: 200_000) },
            fromDeviceBps: 100e6, toDeviceBps: null);

        Assert.True(scored[0].IsViable);
        Assert.NotNull(scored[0].FromDeviceEfficiency);
        Assert.Null(scored[0].ToDeviceEfficiency);
    }

    [Fact]
    public void A_candidate_with_no_rates_is_not_a_candidate()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { new WifiFitCandidate("ap", "5ghz", null, null, -60, 3) },
            fromDeviceBps: 100e6, toDeviceBps: 50e6);

        Assert.False(scored[0].IsViable);
    }

    /// <summary>
    /// The measured case: Front Yard at 79.5 Mbps against a 72 Mbps TX was the correct association
    /// and was rejected at 110%. PHY is sampled every ten seconds and moves during a test, so
    /// modest overshoot is sampling noise rather than impossibility.
    /// </summary>
    [Fact]
    public void Throughput_slightly_over_the_phy_is_sampling_noise_not_impossibility()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { Ap("frontyard", txKbps: 72_000, rxKbps: 8_000) },
            fromDeviceBps: 8.2e6, toDeviceBps: 79.5e6);

        Assert.True(scored[0].IsViable);
    }

    /// <summary>
    /// An access point that carried nothing during the window did not serve the test. Ranked rather
    /// than rejected: a roam mid-test resets the byte counters, leaving both sides with none.
    /// </summary>
    [Fact]
    public void An_access_point_that_carried_traffic_outranks_one_that_carried_none()
    {
        var idle = Ap("idle", txKbps: 200_000, rxKbps: 200_000) with { ObservedThroughputBps = 0 };
        var busy = Ap("busy", txKbps: 200_000, rxKbps: 200_000) with { ObservedThroughputBps = 40e6 };

        var scored = SpeedTestWifiFit.Score(new[] { idle, busy }, fromDeviceBps: 50e6, toDeviceBps: 50e6);

        Assert.Equal("busy", scored[0].Candidate.ApMac);
    }

    [Fact]
    public void Carrying_no_traffic_does_not_disqualify_when_it_is_the_only_candidate()
    {
        var scored = SpeedTestWifiFit.Score(
            new[] { Ap("only", txKbps: 200_000, rxKbps: 200_000) with { ObservedThroughputBps = 0 } },
            fromDeviceBps: 50e6, toDeviceBps: 50e6);

        Assert.True(scored[0].IsViable);
    }

    /// <summary>No candidates at all is the only case that leaves the realtime result standing.</summary>
    [Fact]
    public void No_candidates_returns_null_so_the_caller_keeps_the_realtime_result()
        => Assert.Null(SpeedTestWifiFit.Best(
            Array.Empty<WifiFitCandidate>(), fromDeviceBps: 5e6, toDeviceBps: 5e6));

    /// <summary>
    /// Coverage outranks efficiency. The access point seen through the window held the client, even
    /// where another one's rates happen to divide more neatly into the measurement.
    /// </summary>
    [Fact]
    public void More_points_in_the_window_outranks_a_tidier_ratio()
    {
        var brief = Ap("brief", txKbps: 150_000, rxKbps: 150_000, points: 1);
        var present = Ap("present", txKbps: 900_000, rxKbps: 900_000, points: 4);

        var scored = SpeedTestWifiFit.Score(new[] { brief, present }, fromDeviceBps: 100e6, toDeviceBps: 100e6);

        Assert.Equal("present", scored[0].Candidate.ApMac);
    }
}

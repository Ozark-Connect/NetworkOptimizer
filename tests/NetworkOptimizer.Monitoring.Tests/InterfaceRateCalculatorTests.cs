using FluentAssertions;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Tests for SNMP counter-to-rate computation: normal deltas, 32-bit wrap,
/// unchanged-counter holds, genuine resets, and single-sample read glitches.
/// The glitch cases reproduce a corrupt counter read that, untreated, snaps back
/// to a terabit/sec rate on a 2.5G port.
/// </summary>
public class InterfaceRateCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 4, 43, 0, DateTimeKind.Utc);
    private const long TwoPointFiveGbps = 2_500_000_000L;

    private static InterfaceRateCalculator.State Seed(long inO, long outO, DateTime ts) =>
        new(inO, outO, ts);

    [Fact]
    public void FirstSample_SeedsBaseline_NoRate()
    {
        var r = InterfaceRateCalculator.Compute(
            previous: null, inOctets: 1000, outOctets: 2000, now: T0,
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        r.RateInBps.Should().BeNull();
        r.NewState.InOctets.Should().Be(1000);
    }

    [Fact]
    public void NormalDelta_ComputesBitsPerSecond()
    {
        var prev = Seed(0, 0, T0);
        // 1,250,000 bytes in over 10s = 1,000,000 bps.
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 1_250_000, outOctets: 0, now: T0.AddSeconds(10),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        r.RateInBps.Should().BeApproximately(1_000_000, 1);
        r.RateOutBps.Should().Be(0);
        r.NewState.InOctets.Should().Be(1_250_000);
    }

    [Fact]
    public void UnchangedCounters_HoldBaseline_NoRate()
    {
        var prev = Seed(5000, 6000, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 5000, outOctets: 6000, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.CounterUnchanged);
        r.RateInBps.Should().BeNull();
        // Baseline timestamp held at T0 so the next real change spans the true window.
        r.NewState.Timestamp.Should().Be(T0);
    }

    [Fact]
    public void SubSecondElapsed_HoldsBaseline_NoRate()
    {
        var prev = Seed(5000, 6000, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 9000, outOctets: 9000, now: T0.AddSeconds(0.3),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.CounterUnchanged);
        r.RateInBps.Should().BeNull();
        r.NewState.Timestamp.Should().Be(T0);
    }

    [Fact]
    public void ThirtyTwoBitWrap_RecoversDelta_WhenNotHcCounters()
    {
        // prev near the 32-bit ceiling, current wrapped past 0.
        long prevIn = uint.MaxValue - 100; // 4,294,967,195
        var prev = Seed(prevIn, 0, T0);
        // +1000 bytes across the wrap, over 8s = 1000 bytes.
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 900, outOctets: 0, now: T0.AddSeconds(8),
            useHcCounters: false, linkSpeedBps: 1_000_000_000L);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        // delta = (2^32 - (prevIn)) + 900 = 100 + 1 + 900 = 1001 bytes... compute exact:
        long expectedDelta = (900 - prevIn) + (long)uint.MaxValue + 1;
        r.RateInBps.Should().BeApproximately(expectedDelta * 8.0 / 8.0, 1);
    }

    // ─── Genuine reset: two consecutive below-baseline reads ───

    [Fact]
    public void CounterReset_FirstLowRead_IsPending_HoldsBaseline()
    {
        var prev = Seed(6_554_744_931_434, 5_000_000_000, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 1_000_000, outOctets: 800_000, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetPending);
        r.RateInBps.Should().BeNull();
        // Baseline preserved; the low read is remembered as a candidate.
        r.NewState.InOctets.Should().Be(6_554_744_931_434);
        r.NewState.CandidateInOctets.Should().Be(1_000_000);
    }

    [Fact]
    public void CounterReset_SecondLowRead_ConfirmsReset_ReseedsBaseline()
    {
        var prev = Seed(6_554_744_931_434, 5_000_000_000, T0) with
        {
            CandidateInOctets = 1_000_000,
            CandidateOutOctets = 800_000,
            CandidateTimestamp = T0.AddSeconds(5),
        };
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 1_500_000, outOctets: 1_100_000, now: T0.AddSeconds(10),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetConfirmed);
        r.RateInBps.Should().BeNull();
        // Baseline reseeded to the current post-reset sample.
        r.NewState.InOctets.Should().Be(1_500_000);
        r.NewState.CandidateInOctets.Should().BeNull();
    }

    // ─── Single-sample glitch (corrupt read, then snap-back) ───

    [Fact]
    public void GlitchLowThenRecover_AbsorbedWithNoSpike_BaselineNeverPoisoned()
    {
        // A corrupt low read between two true ~6.55 TB reads.
        var baseline = Seed(6_554_744_931_434, 0, T0.AddSeconds(12));

        // Glitch read: counter momentarily reports 0.63 GB.
        var afterGlitch = InterfaceRateCalculator.Compute(
            baseline, inOctets: 625_957_356, outOctets: 0, now: T0.AddSeconds(21),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);
        afterGlitch.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetPending);
        afterGlitch.RateInBps.Should().BeNull();
        afterGlitch.NewState.InOctets.Should().Be(6_554_744_931_434, "baseline must not be poisoned");

        // Next true read snaps back to the real counter (+2.1 MB of real traffic).
        var recovered = InterfaceRateCalculator.Compute(
            afterGlitch.NewState, inOctets: 6_554_747_046_283, outOctets: 0, now: T0.AddSeconds(26),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        recovered.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        // 2,114,849 bytes over 14s ≈ 1.2 Mbps - NOT 10 Tbps.
        recovered.RateInBps.Should().BeLessThan(2_000_000);
        recovered.RateInBps.Should().BeGreaterThan(0);
        recovered.NewState.InOctets.Should().Be(6_554_747_046_283);
        recovered.NewState.CandidateInOctets.Should().BeNull();
    }

    [Fact]
    public void GlitchHigh_ExceedsLinkSpeed_Discarded_BaselineAdvances()
    {
        var prev = Seed(6_554_744_931_434, 0, T0);
        // Corrupt high read: +6.55 TB in 5s would be ~10 Tbps on a 2.5G port.
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 16_554_747_046_283, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        r.RateInBps.Should().BeNull("the bogus rate must not be emitted");
        r.RejectedRateInBps.Should().BeGreaterThan(TwoPointFiveGbps * InterfaceRateCalculator.LinkSpeedToleranceFactor);
        // Baseline advances to the rejected sample so a poisoned baseline can never
        // wedge the interface; the discarded rate is never emitted regardless.
        r.NewState.InOctets.Should().Be(16_554_747_046_283);
    }

    [Fact]
    public void ImplausibleRate_AdvancesBaseline_SoNextSampleRecovers_NeverStuck()
    {
        // A poisoned-low baseline (e.g. from two earlier corrupt low reads that
        // falsely confirmed a reset) makes the next real read look impossible.
        // The interface must recover, not suppress forever.
        var poisoned = Seed(630_000_000, 0, T0);

        var rejected = InterfaceRateCalculator.Compute(
            poisoned, inOctets: 6_554_747_046_283, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);
        rejected.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        rejected.RateInBps.Should().BeNull();
        rejected.NewState.InOctets.Should().Be(6_554_747_046_283, "baseline advanced past the poison");

        // Next clean read computes a normal delta from the corrected baseline.
        var recovered = InterfaceRateCalculator.Compute(
            rejected.NewState, inOctets: 6_554_748_840_227, outOctets: 0, now: T0.AddSeconds(10),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);
        recovered.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        recovered.RateInBps.Should().BeGreaterThan(0);
        recovered.RateInBps.Should().BeLessThan(TwoPointFiveGbps);
    }

    [Fact]
    public void RateWithin40PercentOverLinkSpeed_IsAllowed()
    {
        var prev = Seed(0, 0, T0);
        // 1.3x link speed: jitter over a short window, still plausible.
        long bytes = (long)(TwoPointFiveGbps * 1.3 / 8.0 * 5.0); // 5s worth at 1.3x
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: bytes, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        r.RateInBps.Should().BeApproximately(TwoPointFiveGbps * 1.3, TwoPointFiveGbps * 0.01);
    }

    [Fact]
    public void UnknownLinkSpeed_RateUnderCeiling_IsAllowed()
    {
        // ppp*/gre* report speed 0; the 14 Gbps absolute ceiling applies.
        var prev = Seed(0, 0, T0);
        long bytes = (long)(10_000_000_000d / 8.0 * 5.0); // 10 Gbps for 5s
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: bytes, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: 0);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        r.RateInBps.Should().BeApproximately(10_000_000_000d, 1_000_000);
    }

    [Fact]
    public void UnknownLinkSpeed_RateOverCeiling_IsRejected()
    {
        var prev = Seed(0, 0, T0);
        long bytes = (long)(20_000_000_000d / 8.0 * 5.0); // 20 Gbps, over the 14 Gbps ceiling
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: bytes, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: 0);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        r.RateInBps.Should().BeNull();
    }
}

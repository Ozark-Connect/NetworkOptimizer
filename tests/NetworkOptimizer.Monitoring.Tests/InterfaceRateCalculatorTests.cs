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
    private const long TenGbps = 10_000_000_000L;

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
        // Baseline reseeded to the current post-reset sample, provisionally.
        r.NewState.InOctets.Should().Be(1_500_000);
        r.NewState.CandidateInOctets.Should().BeNull();
        r.NewState.ProvisionalSeed.Should().BeTrue();
        r.IsUntrusted.Should().BeTrue("the confirming read is not stored");
    }

    [Fact]
    public void SnmpAgentRestart_LowNonzeroReads_ConfirmReset_RealCounterReturns_NoPhantomRate()
    {
        // A gateway's eth0 while its SNMP service restarted: two low nonzero reads confirmed a
        // "reset", then the real counter came back. Against the low read that was 404 Mbps.
        var baseline = Seed(856_074_906, 412_000_000, T0);

        var low1 = InterfaceRateCalculator.Compute(
            baseline, 740_241_477, 380_000_000, T0.AddSeconds(11), useHcCounters: true, linkSpeedBps: TenGbps);
        low1.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetPending);

        var low2 = InterfaceRateCalculator.Compute(
            low1.NewState, 740_241_477, 380_000_000, T0.AddSeconds(13), useHcCounters: true, linkSpeedBps: TenGbps);
        low2.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetConfirmed);
        low2.IsUntrusted.Should().BeTrue();

        var back = InterfaceRateCalculator.Compute(
            low2.NewState, 856_103_093, 412_010_000, T0.AddSeconds(15), useHcCounters: true, linkSpeedBps: TenGbps);
        back.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        back.RateInBps.Should().BeNull("the real counter's return is not traffic");
        back.IsUntrusted.Should().BeFalse();

        var next = InterfaceRateCalculator.Compute(
            back.NewState, 856_103_959, 412_010_500, T0.AddSeconds(20), useHcCounters: true, linkSpeedBps: TenGbps);
        next.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        next.RateInBps.Should().BeApproximately(866 * 8.0 / 5.0, 1);
    }

    [Fact]
    public void CounterReset_ConfirmedOnZeroReads_StaysProvisional_NoSpikeOnRecovery()
    {
        // Two all-zero HC reads confirm a "reset"; the next real read must not be
        // differenced against zero (it computed 2.3 Gbps on a 2.5G WAN).
        var prev = Seed(6_771_000_000, 2_000_000_000, T0) with
        {
            CandidateInOctets = 0,
            CandidateOutOctets = 0,
            CandidateTimestamp = T0.AddSeconds(5),
        };
        var confirmed = InterfaceRateCalculator.Compute(
            prev, inOctets: 0, outOctets: 0, now: T0.AddSeconds(10),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        confirmed.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetConfirmed);
        confirmed.NewState.ProvisionalSeed.Should().BeTrue();

        var recovered = InterfaceRateCalculator.Compute(
            confirmed.NewState, inOctets: 6_771_219_346, outOctets: 2_000_500_000, now: T0.AddSeconds(34),
            useHcCounters: true, linkSpeedBps: TwoPointFiveGbps);

        recovered.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        recovered.RateInBps.Should().BeNull();
        recovered.NewState.ProvisionalSeed.Should().BeFalse();
        recovered.NewState.InOctets.Should().Be(6_771_219_346);
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

    // ─── All-zero seed read (corrupt first poll after a cache reset) ───

    [Fact]
    public void ZeroSeed_OnHcCounters_DefersBaseline_NoRate()
    {
        // First poll after the counter cache cleared (e.g. console reboot) returns a
        // corrupt (0,0). It must not become the baseline.
        var r = InterfaceRateCalculator.Compute(
            previous: null, inOctets: 0, outOctets: 0, now: T0,
            useHcCounters: true, linkSpeedBps: TenGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        r.RateInBps.Should().BeNull();
        r.RateOutBps.Should().BeNull();
        r.NewState.ProvisionalSeed.Should().BeTrue("a (0,0) seed is provisional until a real read");
    }

    [Fact]
    public void ZeroSeed_ThenRealCounter_ReseedsWithoutSpike()
    {
        // Reproduces Mac eth5 on 2026-06-17 18:33: a (0,0) read seeded the baseline
        // after a console reboot, then the true ~4.29 GB / ~5.0 GB counters returned
        // ~5.93 s later. Untreated this emitted 5.79 / 6.75 Gbps - under the 10G x1.4
        // ceiling, so it slipped through. It must emit nothing and reseed instead.
        var seed = InterfaceRateCalculator.Compute(
            previous: null, inOctets: 0, outOctets: 0, now: T0,
            useHcCounters: true, linkSpeedBps: TenGbps);
        seed.NewState.ProvisionalSeed.Should().BeTrue();

        var recovered = InterfaceRateCalculator.Compute(
            seed.NewState, inOctets: 4_291_840_953, outOctets: 4_997_948_108, now: T0.AddSeconds(5.93),
            useHcCounters: true, linkSpeedBps: TenGbps);

        recovered.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        recovered.RateInBps.Should().BeNull("the phantom ~5.8/6.7 Gbps snap-back must not be emitted");
        recovered.RateOutBps.Should().BeNull();
        recovered.NewState.InOctets.Should().Be(4_291_840_953, "the real read is now the trusted baseline");
        recovered.NewState.ProvisionalSeed.Should().BeFalse();

        // The next real read computes a normal rate from the now-trusted baseline
        // (~12.36 MB over 5.21 s = ~18.97 Mbps, the actual Mac value at 18:33:13).
        var next = InterfaceRateCalculator.Compute(
            recovered.NewState, inOctets: 4_304_200_890, outOctets: 4_998_357_079, now: T0.AddSeconds(11.14),
            useHcCounters: true, linkSpeedBps: TenGbps);
        next.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        next.RateInBps.Should().BeApproximately(18_970_000, 200_000);
        next.RateInBps.Should().BeLessThan(TenGbps);
    }

    [Fact]
    public void ZeroSeed_ThenAnotherZero_StaysProvisional()
    {
        var seed = InterfaceRateCalculator.Compute(
            previous: null, inOctets: 0, outOctets: 0, now: T0,
            useHcCounters: true, linkSpeedBps: TenGbps);

        var still = InterfaceRateCalculator.Compute(
            seed.NewState, inOctets: 0, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: TenGbps);

        still.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        still.RateInBps.Should().BeNull();
        still.NewState.ProvisionalSeed.Should().BeTrue();
    }

    [Fact]
    public void EstablishedZeroBaseline_StillComputesRate_DeferralIsSeedTimeOnly()
    {
        // A (0,0) that is an ESTABLISHED baseline (ProvisionalSeed false - e.g. a
        // low-speed interface idle since the baseline was taken) must still compute a
        // rate. The deferral applies only to a (0,0) seeded as the first read.
        var prev = Seed(0, 0, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 1_250_000, outOctets: 0, now: T0.AddSeconds(10),
            useHcCounters: true, linkSpeedBps: TenGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        r.RateInBps.Should().BeApproximately(1_000_000, 1);
    }

    // ─── An SNMP agent restarting: all-zero reads on an interface with no known speed ───

    [Fact]
    public void AllZeroRead_OnUnknownSpeed32BitInterface_IsNeverAWrap_NoPhantomRate()
    {
        // A tunnel interface reports no speed, so it is polled as 32-bit and its only ceiling is
        // the absolute one. Wrap arithmetic on a (0,0) read made 2^32 bytes of traffic out of it.
        var prev = Seed(2_182_017, 1_500_000, T0);
        var zero = InterfaceRateCalculator.Compute(
            prev, inOctets: 0, outOctets: 0, now: T0.AddSeconds(8),
            useHcCounters: false, linkSpeedBps: 0);

        zero.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetPending);
        zero.RateInBps.Should().BeNull();
        zero.NewState.InOctets.Should().Be(2_182_017, "the baseline is held");

        var zeroAgain = InterfaceRateCalculator.Compute(
            zero.NewState, 0, 0, T0.AddSeconds(10), useHcCounters: false, linkSpeedBps: 0);
        zeroAgain.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetConfirmed);
        zeroAgain.NewState.ProvisionalSeed.Should().BeTrue("a zero baseline makes the next real read a phantom");

        var back = InterfaceRateCalculator.Compute(
            zeroAgain.NewState, 2_182_917, 1_500_400, T0.AddSeconds(36), useHcCounters: false, linkSpeedBps: 0);
        back.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        back.RateInBps.Should().BeNull();

        var next = InterfaceRateCalculator.Compute(
            back.NewState, 2_183_917, 1_500_400, T0.AddSeconds(38), useHcCounters: false, linkSpeedBps: 0);
        next.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        next.RateInBps.Should().BeApproximately(4_000, 1);
    }

    [Fact]
    public void BaselineAboveThe32BitRange_IsNeverWrapRecovered()
    {
        // 6.1 GB cannot be a 32-bit counter, whatever the interface's speed says.
        var prev = Seed(6_163_152_232, 10, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: 5_000_000_000, outOctets: 20, now: T0.AddSeconds(8),
            useHcCounters: false, linkSpeedBps: 0);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetPending);
        r.RateInBps.Should().BeNull();
    }

    [Fact]
    public void ZeroSeed_OnUnknownSpeedInterface_DefersBaseline()
    {
        var r = InterfaceRateCalculator.Compute(
            previous: null, inOctets: 0, outOctets: 0, now: T0,
            useHcCounters: false, linkSpeedBps: 0);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        r.NewState.ProvisionalSeed.Should().BeTrue();
    }

    [Fact]
    public void ZeroSeed_On32BitCounters_SeedsNormally_NotDeferred()
    {
        // 32-bit counters wrap and a (0,0) is plausible on an idle low-speed interface;
        // a snap-back there exceeds the low link ceiling and is caught by ImplausibleRate.
        // The seed deferral is scoped to 64-bit HC counters, so this seeds normally.
        var r = InterfaceRateCalculator.Compute(
            previous: null, inOctets: 0, outOctets: 0, now: T0,
            useHcCounters: false, linkSpeedBps: 1_000_000_000L);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.SeededBaseline);
        r.NewState.ProvisionalSeed.Should().BeFalse();
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
    public void UnknownLinkSpeed_RateOverCeiling_IsNeverEmitted()
    {
        var prev = Seed(0, 0, T0);
        long bytes = (long)(20_000_000_000d / 8.0 * 5.0); // 20 Gbps, over the 14 Gbps ceiling
        var r = InterfaceRateCalculator.Compute(
            prev, inOctets: bytes, outOctets: 0, now: T0.AddSeconds(5),
            useHcCounters: true, linkSpeedBps: 0);

        // No link speed, so no hold: discarded at once, however close to the ceiling.
        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        r.RateInBps.Should().BeNull();
    }

    [Fact]
    public void TunnelJumpAfterAnSnmpRestart_IsDiscarded_NotHeldIntoTheNextWindow()
    {
        // wgclt2 at a 2 s poll when the gateway's SNMP restarted: a ~4 GB forward jump (16 Gbps).
        // Held, the next window absorbed it and 7.9 Gbps was stored.
        var prev = Seed(10_000_000_000, 5_000_000, T0);
        var jump = InterfaceRateCalculator.Compute(
            prev, 10_000_000_000 + 4_010_000_000, 5_000_100, T0.AddSeconds(2),
            useHcCounters: true, linkSpeedBps: 0);

        jump.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        jump.IsUntrusted.Should().BeTrue();

        var next = InterfaceRateCalculator.Compute(
            jump.NewState, 10_000_000_000 + 4_010_000_000 + 250_000, 5_000_200, T0.AddSeconds(4),
            useHcCounters: true, linkSpeedBps: 0);

        next.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        next.RateInBps.Should().BeApproximately(1_000_000, 1, "only the real 1 Mbps, none of the jump");
    }

    // ─── Line-rate traffic read marginally over the ceiling ───

    [Fact]
    public void LineRateInflatedByTiming_IsHeldOnce_ThenAveragedAcrossBothWindows()
    {
        // A 9.4 Gbps transfer on a 10G port, polled every 2 s. The first counter read carries 3 s
        // of traffic, so it computes 14.1 Gbps - just over the 14 Gbps ceiling.
        const double rate = 9_400_000_000d;
        long BytesFor(double seconds) => (long)(rate / 8.0 * seconds);
        var prev = Seed(1_000_000_000, 50_000_000, T0);

        var early = InterfaceRateCalculator.Compute(
            prev, 1_000_000_000 + BytesFor(3), 50_100_000, T0.AddSeconds(2),
            useHcCounters: true, linkSpeedBps: TenGbps);

        early.Outcome.Should().Be(InterfaceRateCalculator.Outcome.CeilingHeld);
        early.RateInBps.Should().BeNull();
        early.IsUntrusted.Should().BeTrue("a held sample is not stored");
        early.NewState.InOctets.Should().Be(1_000_000_000, "the baseline is held");
        early.NewState.Timestamp.Should().Be(T0);

        var next = InterfaceRateCalculator.Compute(
            early.NewState, 1_000_000_000 + BytesFor(4), 50_200_000, T0.AddSeconds(4),
            useHcCounters: true, linkSpeedBps: TenGbps);

        next.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        next.RateInBps.Should().BeApproximately(rate, 1_000);
        next.NewState.CeilingHeld.Should().BeFalse();
    }

    [Fact]
    public void FarOverTheHoldBand_IsDiscardedAtOnce()
    {
        var prev = Seed(0, 0, T0);
        long bytes = (long)(TenGbps * 3.0 / 8.0 * 2.0); // 30 Gbps on a 10G port
        var r = InterfaceRateCalculator.Compute(
            prev, bytes, 0, T0.AddSeconds(2), useHcCounters: true, linkSpeedBps: TenGbps);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        r.NewState.InOctets.Should().Be(bytes);
    }

    [Fact]
    public void WrapRecoveredDelta_OverTheCeiling_IsNeverHeld()
    {
        // A longer window would bring a false wrap under the ceiling, so it is discarded at once.
        var prev = Seed(3_000_000_000, 1_000, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, 500_000_000, 2_000, T0.AddSeconds(60), useHcCounters: false, linkSpeedBps: 100_000_000L);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ImplausibleRate);
        r.RateInBps.Should().BeNull();
    }

    // ─── 32-bit wrap needs the counter to go back by more than half its range ───

    [Fact]
    public void SlightlyBackwardRead_On32BitUnknownSpeed_IsNotAWrap_NoPhantomRate()
    {
        // An access point's wifi0 read a little below its baseline in both directions. Wrapped,
        // that is ~2^32 bytes each way: 6.9 Gbps at a 5 s poll, under the 14 Gbps ceiling.
        var prev = Seed(3_000_000_000, 2_900_000_000, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, 2_999_000_000, 2_899_000_000, T0.AddSeconds(5), useHcCounters: false, linkSpeedBps: 0);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.ResetPending);
        r.RateInBps.Should().BeNull();
        r.NewState.InOctets.Should().Be(3_000_000_000, "the baseline is held");
    }

    [Fact]
    public void ThirtyTwoBitWrap_InOneDirectionOnly_RecoversThatDirection()
    {
        var prev = Seed(uint.MaxValue - 1_000, 2_000_000, T0);
        var r = InterfaceRateCalculator.Compute(
            prev, 4_000, 2_500_000, T0.AddSeconds(5), useHcCounters: false, linkSpeedBps: 0);

        r.Outcome.Should().Be(InterfaceRateCalculator.Outcome.Normal);
        r.RateInBps.Should().BeApproximately(5_001 * 8.0 / 5.0, 1);
        r.RateOutBps.Should().BeApproximately(500_000 * 8.0 / 5.0, 1);
    }
}

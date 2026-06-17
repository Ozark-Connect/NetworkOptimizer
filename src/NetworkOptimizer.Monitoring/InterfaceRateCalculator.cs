namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Pure rate-from-counter computation for SNMP interface octet counters.
/// Cumulative counters are messy in practice: they wrap (32-bit), stall between
/// device-side refreshes, reset on reboot/firmware upgrade, and occasionally
/// return a single corrupt read. This calculator turns a sequence of
/// (InOctets, OutOctets, timestamp) samples into bits-per-second rates while
/// refusing to emit physically-impossible spikes or poison its own baseline off
/// one bad sample.
/// </summary>
public static class InterfaceRateCalculator
{
    /// <summary>
    /// Headroom over link speed before a computed rate is treated as a glitch.
    /// A short polling window plus clock jitter can legitimately push a single
    /// sample up to ~40% over line rate, so only clearly-impossible values are
    /// rejected.
    /// </summary>
    public const double LinkSpeedToleranceFactor = 1.4;

    /// <summary>
    /// Absolute rate ceiling used when the interface's link speed is unknown
    /// (virtual interfaces such as ppp*/gre* report speed 0). These carry WAN /
    /// tunnel traffic that tops out around 10 Gbps in the wild, so 14 Gbps leaves
    /// headroom while still rejecting corrupt reads.
    /// </summary>
    public const double AbsoluteCeilingBps = 14_000_000_000d; // 14 Gbps

    public enum Outcome
    {
        /// <summary>Rate computed and emitted normally.</summary>
        Normal,

        /// <summary>Counters unchanged since the last poll; baseline held, no rate.</summary>
        CounterUnchanged,

        /// <summary>First sample for this interface; baseline seeded, no rate.</summary>
        SeededBaseline,

        /// <summary>
        /// Counter read below the baseline once. Could be a real reset or a
        /// single corrupt read - baseline held, candidate remembered, no rate.
        /// </summary>
        ResetPending,

        /// <summary>
        /// A second consecutive below-baseline read confirmed a genuine counter
        /// reset; baseline reseeded to the current sample, no rate for the
        /// boundary. Worth an INFO log.
        /// </summary>
        ResetConfirmed,

        /// <summary>
        /// Computed rate exceeded the link-speed ceiling - a corrupt read or a
        /// snap-back. Rate discarded; baseline advances to this sample so a bad
        /// baseline can't wedge the interface. Worth a WARN log.
        /// </summary>
        ImplausibleRate,
    }

    /// <summary>
    /// Per-interface counter state carried between polls: the last trusted
    /// baseline plus an optional "reset candidate" (a below-baseline reading
    /// awaiting confirmation before we trust it). <see cref="ProvisionalSeed"/>
    /// marks a baseline seeded from an all-zero read that is not yet trusted: the
    /// real baseline is established from the first nonzero read instead (see Compute).
    /// </summary>
    public readonly record struct State(
        long InOctets,
        long OutOctets,
        DateTime Timestamp,
        long? CandidateInOctets = null,
        long? CandidateOutOctets = null,
        DateTime? CandidateTimestamp = null,
        bool ProvisionalSeed = false);

    public readonly record struct Result(
        double? RateInBps,
        double? RateOutBps,
        State NewState,
        Outcome Outcome,
        double? RejectedRateInBps = null,
        double? RejectedRateOutBps = null);

    /// <summary>
    /// Compute the rate for a new counter sample against the previous state.
    /// </summary>
    /// <param name="previous">Prior state for this interface, or null on first sight.</param>
    /// <param name="inOctets">Current cumulative inbound octet counter.</param>
    /// <param name="outOctets">Current cumulative outbound octet counter.</param>
    /// <param name="now">Timestamp of this sample.</param>
    /// <param name="useHcCounters">
    /// True when the device exposes 64-bit high-capacity counters (no 32-bit
    /// wrap recovery applied).
    /// </param>
    /// <param name="linkSpeedBps">
    /// Interface link speed in bps for the plausibility ceiling, or 0 if unknown.
    /// </param>
    public static Result Compute(
        State? previous,
        long inOctets,
        long outOctets,
        DateTime now,
        bool useHcCounters,
        long linkSpeedBps)
    {
        var fresh = new State(inOctets, outOctets, now);

        if (previous is not { } prev)
        {
            // An all-zero 64-bit counter read is almost never a real baseline - it is a
            // corrupt poll (or a device caught mid-boot). Trusting it as the baseline
            // makes the next true read snap back to a near-link-speed phantom that the
            // link-speed ceiling is far too loose to reject on a fast port carrying a
            // slow circuit: a 10G WAN port seeded a (0,0) read after a console reboot,
            // and the recovery read computed ~6.7 Gbps - under the 14 Gbps ceiling, so it
            // was emitted. Seed provisionally and wait for the first trustworthy nonzero
            // read before establishing the baseline.
            if (useHcCounters && inOctets == 0 && outOctets == 0)
                return new Result(null, null, fresh with { ProvisionalSeed = true }, Outcome.SeededBaseline);
            return new Result(null, null, fresh, Outcome.SeededBaseline);
        }

        // A provisional seed (from an earlier all-zero read) is not a trusted baseline,
        // so never compute a rate against it: a nonzero read now establishes the real
        // baseline; another all-zero read keeps waiting. Either way no rate is emitted.
        if (prev.ProvisionalSeed)
        {
            if (inOctets == 0 && outOctets == 0)
                return new Result(null, null, fresh with { ProvisionalSeed = true }, Outcome.SeededBaseline);
            return new Result(null, null, fresh, Outcome.SeededBaseline);
        }

        var elapsed = (now - prev.Timestamp).TotalSeconds;
        if (elapsed <= 0.5)
            // Too soon, or the clock moved backwards: hold the baseline, no rate.
            return new Result(null, null, prev, Outcome.CounterUnchanged);

        long deltaIn = inOctets - prev.InOctets;
        long deltaOut = outOctets - prev.OutOctets;

        // 32-bit wrap recovery for low-speed interfaces that only expose the
        // 32-bit ifInOctets/ifOutOctets counters.
        if (!useHcCounters)
        {
            if (deltaIn < 0) deltaIn += (long)uint.MaxValue + 1;
            if (deltaOut < 0) deltaOut += (long)uint.MaxValue + 1;
        }

        if (deltaIn < 0 || deltaOut < 0)
        {
            // Counter went backwards. Either a genuine reset (reboot / firmware
            // upgrade) or a single corrupt SNMP read - indistinguishable from one
            // sample. Do NOT reseed the baseline off this reading: a corrupt low
            // value would make the next clean poll snap back to a terabit/sec
            // spike. Hold the last good baseline and remember this as a reset
            // candidate; only a second below-baseline poll confirms a real reset.
            if (prev.CandidateTimestamp is not null)
                return new Result(null, null, fresh, Outcome.ResetConfirmed);

            var holding = prev with
            {
                CandidateInOctets = inOctets,
                CandidateOutOctets = outOctets,
                CandidateTimestamp = now,
            };
            return new Result(null, null, holding, Outcome.ResetPending);
        }

        // Counter advanced (or held flat). Any pending reset candidate is now
        // disproven - the counter recovered above the baseline, so the earlier
        // dip was a transient glitch. Drop the candidate and carry on.
        if (deltaIn == 0 && deltaOut == 0)
        {
            // Device hasn't refreshed its counters yet. Hold the baseline so the
            // next real change computes delta/elapsed over the true window
            // (prevents an alternating 0 / 2x sawtooth).
            var held = prev with
            {
                CandidateInOctets = null,
                CandidateOutOctets = null,
                CandidateTimestamp = null,
            };
            return new Result(null, null, held, Outcome.CounterUnchanged);
        }

        var rateInBps = deltaIn * 8.0 / elapsed;
        var rateOutBps = deltaOut * 8.0 / elapsed;

        var ceiling = linkSpeedBps > 0 ? linkSpeedBps * LinkSpeedToleranceFactor : AbsoluteCeilingBps;
        if (rateInBps > ceiling || rateOutBps > ceiling)
        {
            // Physically impossible for this link - a corrupt high read or a
            // snap-back from a momentarily-low counter. The rate is discarded
            // (surfaced on RejectedRate*Bps for logging only; callers must not
            // emit it). Advance the baseline to this sample anyway: holding the
            // old baseline would wedge the interface forever if that baseline is
            // itself bad (a never-recovering "every read looks impossible" loop).
            // Advancing means the next clean sample computes a normal delta, and
            // no rate is ever emitted from a discarded sample regardless.
            return new Result(
                null,
                null,
                fresh,
                Outcome.ImplausibleRate,
                rateInBps,
                rateOutBps);
        }

        return new Result(rateInBps, rateOutBps, fresh, Outcome.Normal);
    }
}

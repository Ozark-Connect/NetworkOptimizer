namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Recognises the SFP PON ONT stick's DDM read artifact: temperature, RX power and TX bias all
/// jump together for one poll and come straight back. Measured on a live GPON stick over 30 days -
/// 19 events, temperature moving 3 to 28 °C and returning, against a real range of 8 °C across the
/// whole month.
///
/// RX genuinely does track temperature, at 0.048 dB/°C (r = 0.72 on hourly means), which is
/// residual DDM temperature-compensation error rather than a change in received light. So the
/// artifact is not identified by RX moving with temperature - that is normal - but by the RATE.
/// Nothing thermal moves this far inside one 5-minute poll and returns.
///
/// PON sticks only. Active Ethernet ONTs and ordinary optics do not show it.
///
/// Not a duplicate of ISP Health's OpticalSampleStats, which rejects by distance from a window's
/// median temperature and needs the whole window in hand. This judges one poll by its neighbours,
/// which is what a streaming alert and a chart point can actually do. Two rules, two consumers.
/// </summary>
public static class SfpDdmSpikeFilter
{
    /// <summary>Temperature excursion that no real 5-minute movement reaches. Normal deltas top out at 2 °C.</summary>
    public const double TempJumpC = 5.0;

    /// <summary>
    /// RX excursion required alongside it. Below 0.5 dB co-movement is the normal state, not a
    /// signature: 427 samples in the 1-2 °C band already have RX moving with temperature.
    /// </summary>
    public const double RxJumpDbm = 0.5;

    /// <summary>
    /// Whether <paramref name="curTemp"/>/<paramref name="curRx"/> jumped jointly away from the
    /// previous sample. A candidate only: the next sample decides whether it was the artifact or
    /// the start of something real.
    /// </summary>
    public static bool IsJointJump(double prevTemp, double prevRx, double curTemp, double curRx)
    {
        var dTemp = curTemp - prevTemp;
        var dRx = curRx - prevRx;
        return Math.Abs(dTemp) >= TempJumpC
            && Math.Abs(dRx) >= RxJumpDbm
            && Math.Sign(dTemp) == Math.Sign(dRx);
    }

    /// <summary>
    /// Whether the middle sample is the artifact: a joint jump away from the sample before AND the
    /// sample after, in the same direction both times. A real thermal event ramps and stays, so it
    /// fails the second half.
    /// </summary>
    public static bool IsArtifact(
        double prevTemp, double prevRx,
        double midTemp, double midRx,
        double nextTemp, double nextRx)
        => IsJointJump(prevTemp, prevRx, midTemp, midRx)
        && IsJointJump(nextTemp, nextRx, midTemp, midRx);
}

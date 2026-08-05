using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// How hard to probe a WAN whose bandwidth costs money.
/// <para>
/// Continuous probing is not free: 25 targets at the 10s default is about 5.4 GB a month in and
/// out, which is most of a small satellite or cellular plan spent on measurement. The saving comes
/// from probing FEWER things LESS often - never from smaller packets, because a shrunken ICMP
/// payload measures a path nobody else is using and the number stops being comparable to anything.
/// </para>
/// <para>
/// Two independent signals each cost a rung, and they stack. The access technology is a standing
/// property of the link - satellite, cellular and fixed wireless are metered far more often than
/// not - while a Data Usage config the operator has enabled is them telling us outright that this
/// WAN has a cap. A capped cable line lands on rung 1 the same as an uncapped satellite one, which
/// is the point: the rung describes what the traffic costs, not what the medium is.
/// </para>
/// </summary>
public static class MeteredProbePolicy
{
    /// <summary>The cadence an unmetered WAN probes at, unchanged from before any of this existed.</summary>
    public const int DefaultIntervalSeconds = 10;

    /// <param name="Rung">0 unmetered, 1 constrained, 2 tight.</param>
    /// <param name="MaxAutoEnabled">
    /// How many discovered targets to tick on by default, or null for no limit. A cap never
    /// disables anything the operator turned on themselves - it only decides what arrives ticked.
    /// </param>
    /// <param name="PollIntervalSeconds">The cadence targets on this WAN are given.</param>
    public sealed record Plan(int Rung, int? MaxAutoEnabled, int PollIntervalSeconds);

    /// <summary>
    /// Technologies metered often enough to assume it. Wireline is not here: DSL and PPPoE are
    /// rarely capped, and Unknown/Other stay unconstrained deliberately - degrading monitoring
    /// because nobody set the technology would punish the sites least able to notice.
    /// </summary>
    public static bool IsLimitedTechnology(AccessTechnology technology) =>
        technology is AccessTechnology.FixedWireless
            or AccessTechnology.Satellite
            or AccessTechnology.Cellular;

    /// <summary>The plan for a WAN, from its technology and whether Data Usage is tracking it.</summary>
    public static Plan For(AccessTechnology technology, bool dataUsageEnabled)
    {
        var rung = (IsLimitedTechnology(technology) ? 1 : 0) + (dataUsageEnabled ? 1 : 0);
        return rung switch
        {
            0 => new Plan(0, null, DefaultIntervalSeconds),
            1 => new Plan(1, 15, 30),
            _ => new Plan(2, 8, 60),
        };
    }

    /// <summary>
    /// Estimated monthly ICMP volume for a plan, in GB across both directions - what the rungs are
    /// chosen against. Five 84-byte echoes per cycle, each answered: 56-byte payload plus ICMP and
    /// IP headers, which is the standard ping this deliberately does not shrink.
    /// </summary>
    public static double EstimatedMonthlyGb(int targetCount, int pollIntervalSeconds)
    {
        if (targetCount <= 0 || pollIntervalSeconds <= 0) return 0;
        const double bytesPerCycleEachWay = 5 * 84;
        var cyclesPerMonth = 30d * 24 * 60 * 60 / pollIntervalSeconds;
        return targetCount * cyclesPerMonth * bytesPerCycleEachWay * 2 / 1_000_000_000d;
    }
}

using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Pure logic for the channel recommendation outcome memory: attributing metrics to the
/// config that was live at a given time, building soak-period state from channel-change
/// events, and merging long-term persisted outcomes into the recent per-channel stress map.
/// </summary>
public static class ChannelMemoryHelper
{
    /// <summary>
    /// How long a fresh channel change must soak before the optimizer may recommend hopping
    /// back to a channel the radio just left. One week covers a full weekly usage cycle and
    /// gives the outcome memory enough attributed samples to judge the new channel on
    /// measured data instead of inference. Applies to every band.
    /// </summary>
    public static readonly TimeSpan SoakPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// Minimum effective (age-decayed) sample weight before a long-term outcome is trusted to
    /// stand in as measured stress for a channel - roughly half a day of fresh residency,
    /// enough to average out a burst without demanding a full day. As evidence ages past the
    /// half-life its effective weight shrinks, so a channel whose record has mostly decayed
    /// falls back to "unknown" and picks the uncertainty penalty back up naturally.
    /// </summary>
    public const int MinLongTermSamples = 12;

    /// <summary>
    /// Half-life for aging long-term outcomes: a bucket's weight halves every 60 days, so
    /// month-old evidence speaks nearly at full strength while a five-month-old outcome
    /// contributes ~25% - the RF neighborhood drifts, and the average should tilt toward
    /// whatever was measured most recently.
    /// </summary>
    public static readonly TimeSpan OutcomeHalfLife = TimeSpan.FromDays(60);

    /// <summary>
    /// How far back persisted outcomes are read when feeding the engine. Beyond this the RF
    /// neighborhood has likely drifted too much for old outcomes to speak for a channel
    /// (and at three half-lives the decayed weight is nearly gone anyway).
    /// </summary>
    public static readonly TimeSpan LongTermOutcomeWindow = TimeSpan.FromDays(180);

    /// <summary>
    /// Determine which channel an AP was on at a given timestamp by walking the
    /// channel change event timeline backwards.
    /// </summary>
    /// <param name="timestamp">Time the metric sample was taken</param>
    /// <param name="events">Channel change events for one AP and band, sorted chronologically</param>
    /// <param name="currentChannel">The radio's current channel (used when no events exist)</param>
    public static int GetChannelAtTime(
        DateTimeOffset timestamp,
        List<ChannelChangeEvent> events,
        int currentChannel)
    {
        // Walk events in reverse to find the most recent change before this timestamp
        for (int i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Timestamp <= timestamp)
                return events[i].NewChannel;
        }

        // Before any recorded change: use the first event's PreviousChannel if available
        if (events.Count > 0)
            return events[0].PreviousChannel;

        // No change events at all: assume current channel
        return currentChannel;
    }

    /// <summary>
    /// Build soak-period state for one AP radio from its channel-change events (any order,
    /// duplicates tolerated - the same change may arrive from both the UniFi system log and
    /// the persisted change log). Returns null when nothing is soaking: no change happened
    /// within the window, or every recently-left channel is the current channel again.
    /// </summary>
    /// <param name="events">Channel change events for one AP and band</param>
    /// <param name="currentChannel">The radio's current channel - never soaked</param>
    /// <param name="now">Current time (UTC)</param>
    public static ChannelSoakInfo? BuildSoakInfo(
        IEnumerable<ChannelChangeEvent> events,
        int currentChannel,
        DateTimeOffset now)
    {
        var windowStart = now - SoakPeriod;
        var soaked = new HashSet<int>();
        DateTimeOffset lastChange = DateTimeOffset.MinValue;

        foreach (var evt in events)
        {
            if (evt.Timestamp < windowStart || evt.Timestamp > now) continue;
            if (evt.Timestamp > lastChange) lastChange = evt.Timestamp;
            if (evt.PreviousChannel > 0 && evt.PreviousChannel != currentChannel)
                soaked.Add(evt.PreviousChannel);
        }

        if (soaked.Count == 0) return null;

        return new ChannelSoakInfo
        {
            SoakedChannels = soaked,
            LastChangeAt = lastChange,
            SoakEndsAt = lastChange + SoakPeriod
        };
    }

    /// <summary>
    /// Merge long-term persisted outcomes into the recent (UniFi metrics window) per-channel
    /// stress map for one AP radio. Recent data wins for channels it covers - it reflects
    /// today's RF neighborhood; the memory fills in channels the radio sat on longer ago, so
    /// a previously-tried channel keeps its measured ground truth instead of being scored on
    /// inference. Buckets are matched on the radio's current width (plus unknown-width
    /// buckets, which predate a width observation).
    ///
    /// Older evidence counts for less: each bucket's weight is its sample count decayed by
    /// <see cref="OutcomeHalfLife"/>, so when a channel was tried at two different times the
    /// average tilts toward the newer measurement. Channels whose total effective weight
    /// falls below <paramref name="minSampleCount"/> are ignored - fully-aged memory reverts
    /// to "unknown channel" rather than whispering stale numbers with full authority.
    /// </summary>
    /// <param name="recentStress">Per-channel stress from the recent metrics window; may be null</param>
    /// <param name="longTermBuckets">Persisted outcome buckets for the same AP and band</param>
    /// <param name="currentWidthMhz">The radio's current channel width</param>
    /// <param name="now">Current time (UTC), the reference for age decay</param>
    /// <param name="minSampleCount">Minimum effective (decayed) sample weight for a memory channel to count</param>
    /// <returns>The merged map, or null when neither source has data</returns>
    public static Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>? MergeLongTermOutcomes(
        Dictionary<int, (double Utilization, double Interference, double TxRetryPct)>? recentStress,
        IEnumerable<ChannelOutcomeBucket> longTermBuckets,
        int currentWidthMhz,
        DateTimeOffset now,
        int minSampleCount = MinLongTermSamples)
    {
        Dictionary<int, (double, double, double)>? merged = recentStress != null
            ? new Dictionary<int, (double, double, double)>(recentStress)
            : null;

        var byChannel = longTermBuckets
            .Where(b => b.WidthMhz == 0 || b.WidthMhz == currentWidthMhz)
            .GroupBy(b => b.Channel);

        foreach (var group in byChannel)
        {
            if (merged != null && merged.ContainsKey(group.Key)) continue;

            double effectiveWeight = 0, utilSum = 0, interfSum = 0, txRetrySum = 0;
            foreach (var bucket in group)
            {
                var ageDays = Math.Max(0, (now - bucket.LastSampleAt).TotalDays);
                var decay = Math.Pow(0.5, ageDays / OutcomeHalfLife.TotalDays);
                effectiveWeight += bucket.SampleCount * decay;
                utilSum += bucket.UtilizationSum * decay;
                interfSum += bucket.InterferenceSum * decay;
                txRetrySum += bucket.TxRetrySum * decay;
            }

            if (effectiveWeight < minSampleCount) continue;

            merged ??= new Dictionary<int, (double, double, double)>();
            merged[group.Key] = (
                utilSum / effectiveWeight,
                interfSum / effectiveWeight,
                txRetrySum / effectiveWeight);
        }

        return merged;
    }
}

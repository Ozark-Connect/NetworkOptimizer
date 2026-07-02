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
    /// back to a channel the radio just left. 16 hours is enough for real-world load
    /// (including an evening peak, for changes applied during the day) to expose problems
    /// with the new channel, without freezing the plan for days; a genuinely bad move
    /// escapes earlier via the catastrophic-score gate. Applies to every band.
    /// </summary>
    public static readonly TimeSpan SoakPeriod = TimeSpan.FromHours(16);

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
    /// Half-life for aging remembered neighbor sightings - much shorter than the outcome
    /// half-life because neighbor networks churn (APs move, get reconfigured, hotspots come
    /// and go) far faster than a channel's aggregate character drifts.
    /// </summary>
    public static readonly TimeSpan NeighborHalfLife = TimeSpan.FromDays(14);

    /// <summary>
    /// How far back remembered neighbor sightings are read and merged: three half-lives,
    /// where a sighting's confidence has decayed to 12.5% and it effectively no longer
    /// speaks. Keeps the fetch and the decay cutoff in one place.
    /// </summary>
    public static readonly TimeSpan NeighborMemoryWindow = TimeSpan.FromDays(42);

    /// <summary>
    /// Minimum (age × persistence) confidence for a remembered sighting to be merged. 0.125 =
    /// three age half-lives at full persistence, matching <see cref="NeighborMemoryWindow"/>.
    /// </summary>
    public const double MinNeighborConfidence = 0.125;

    /// <summary>
    /// Sighting count at which a remembered neighbor is trusted at full weight. Collection runs
    /// every few hours, so ~3 sightings means the neighbor has been seen consistently rather
    /// than as a one-off (a guest hotspot, a device passing through, a neighbor since departed).
    /// Below it, confidence - and thus both the neighbor's interference weight and the
    /// observation credit it grants - scales down proportionally, so transient sightings can't
    /// accumulate into phantom load on a channel. Tunable.
    /// </summary>
    public const int MinNeighborSightingsForFullWeight = 3;

    /// <summary>
    /// Weakest neighbor signal worth persisting. A few dB below the CCA threshold (-82 dBm,
    /// where radios actually defer) so borderline neighbors whose signal fluctuates aren't
    /// forgotten, while genuine noise-floor sightings don't accumulate rows.
    /// </summary>
    public const int MinPersistedNeighborSignalDbm = -90;

    /// <summary>How long a neighbor sighting row is kept after its last sighting.</summary>
    public const int NeighborRetentionDays = 60;

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

    /// <summary>
    /// Fold remembered neighbor sightings into the pooled live scan results, so a serving
    /// radio keeps neighbor evidence for channels it isn't currently on - once it moves, its
    /// live scan forgets the old channel's neighborhood within the hour. Remembered neighbors
    /// enter with an age-decayed <see cref="NeighborNetwork.Confidence"/> (half-life
    /// <see cref="NeighborHalfLife"/>) so they count for less as they age, and are dropped
    /// entirely below <see cref="MinNeighborConfidence"/>.
    ///
    /// A remembered sighting is suppressed when the live picture supersedes it: the BSSID is
    /// currently seen on a DIFFERENT channel anywhere on the band (it moved - the old row is
    /// obsolete for everyone), or the observing AP itself already sees it live (the live
    /// sighting carries the same evidence at full confidence). A same-channel sighting from a
    /// radio that can't currently hear the neighbor is kept - it is a real per-observer
    /// vantage that triangulation from the live observer would understate.
    ///
    /// Sightings for (AP, band) pairs with no scan entry are ignored - a synthetic scan entry
    /// would make the engine believe it has live scan data it doesn't have. Returns new scan
    /// objects; the inputs are not mutated.
    /// </summary>
    /// <param name="pooledScans">Live scan results, already pooled over the rolling sighting window</param>
    /// <param name="remembered">Persisted sightings for all APs and bands</param>
    /// <param name="now">Current time (UTC), the reference for age decay</param>
    public static List<ChannelScanResult> MergeRememberedNeighbors(
        List<ChannelScanResult> pooledScans,
        IReadOnlyCollection<RememberedNeighborSighting> remembered,
        DateTimeOffset now)
    {
        if (remembered.Count == 0) return pooledScans;

        // Live picture per band: which channels each BSSID is currently seen on, and which
        // APs currently see it (for the same-observer suppression).
        var liveChannels = new Dictionary<(RadioBand Band, string Bssid), HashSet<int>>();
        var liveObservers = new HashSet<(RadioBand Band, string ApMac, string Bssid)>();
        foreach (var scan in pooledScans)
        {
            var apKey = scan.ApMac.ToLowerInvariant();
            foreach (var nb in scan.Neighbors)
            {
                if (string.IsNullOrEmpty(nb.Bssid)) continue;
                var bssidKey = nb.Bssid.ToLowerInvariant();
                if (!liveChannels.TryGetValue((scan.Band, bssidKey), out var channels))
                {
                    channels = new HashSet<int>();
                    liveChannels[(scan.Band, bssidKey)] = channels;
                }
                channels.Add(nb.Channel);
                liveObservers.Add((scan.Band, apKey, bssidKey));
            }
        }

        var rememberedByRadio = remembered
            .ToLookup(s => (s.Band, ApMac: s.ApMac.ToLowerInvariant()));

        var mergedScans = new List<ChannelScanResult>(pooledScans.Count);
        foreach (var scan in pooledScans)
        {
            var apKey = scan.ApMac.ToLowerInvariant();
            List<NeighborNetwork>? added = null;

            foreach (var sighting in rememberedByRadio[(scan.Band, apKey)])
            {
                // Confidence combines recency (age half-life) with persistence (how many
                // cycles the neighbor has actually been seen). A durable neighbor counts at
                // full recency weight; a one-off is scaled down so it can't inflate a channel.
                var ageDays = Math.Max(0, (now - sighting.LastSeenAt).TotalDays);
                var ageDecay = Math.Pow(0.5, ageDays / NeighborHalfLife.TotalDays);
                var persistence = Math.Min(1.0, (double)sighting.SightingCount / MinNeighborSightingsForFullWeight);
                var confidence = ageDecay * persistence;
                if (confidence < MinNeighborConfidence) continue;

                var bssidKey = sighting.Bssid.ToLowerInvariant();
                if (liveChannels.TryGetValue((scan.Band, bssidKey), out var channels))
                {
                    if (!channels.Contains(sighting.Channel)) continue;
                    if (liveObservers.Contains((scan.Band, apKey, bssidKey))) continue;
                }

                added ??= new List<NeighborNetwork>();
                added.Add(new NeighborNetwork
                {
                    Ssid = sighting.Ssid ?? string.Empty,
                    Bssid = sighting.Bssid,
                    Channel = sighting.Channel,
                    Width = sighting.WidthMhz > 0 ? sighting.WidthMhz : null,
                    Signal = sighting.SignalDbm,
                    LastSeen = sighting.LastSeenAt,
                    Confidence = confidence
                });
            }

            if (added == null)
            {
                mergedScans.Add(scan);
                continue;
            }

            mergedScans.Add(new ChannelScanResult
            {
                ApMac = scan.ApMac,
                ApName = scan.ApName,
                Band = scan.Band,
                ScanTime = scan.ScanTime,
                Channels = scan.Channels,
                Neighbors = scan.Neighbors.Concat(added).ToList()
            });
        }

        return mergedScans;
    }
}

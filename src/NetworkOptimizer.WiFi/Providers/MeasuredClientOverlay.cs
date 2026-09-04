using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Providers;

/// <summary>
/// Lays AP-measured readings over the console's client snapshots, per access point.
///
/// An overlay rather than a replacement: the console supplies identity, ownership and history the
/// access point never sees (display name, manufacturer, guest and blocked state, MLO link detail),
/// so only the values the AP actually measured are taken from it. A client whose access point has
/// no measurement is left exactly as the console built it.
/// </summary>
public static class MeasuredClientOverlay
{
    /// <summary>
    /// Applies the overlay in place and returns the same list. An empty
    /// <paramref name="measuredByAp"/> returns the console list untouched.
    /// </summary>
    public static List<WirelessClientSnapshot> Apply(
        List<WirelessClientSnapshot> consoleClients,
        IReadOnlyDictionary<string, IReadOnlyList<MeasuredWirelessClient>>? measuredByAp)
    {
        if (measuredByAp == null || measuredByAp.Count == 0) return consoleClients;

        var index = BuildIndex(consoleClients);

        foreach (var (apMac, measuredClients) in measuredByAp)
        {
            var ap = Normalize(apMac);
            foreach (var measured in measuredClients)
            {
                if (measured.Band == RadioBand.Unknown || measured.Signal is null) continue;
                if (!index.TryGetValue(Normalize(measured.Mac), out var snapshot)) continue;

                // A console record pointing at a different access point is left alone: the readings
                // are per access point, and moving the client here would invent a roam.
                if (!string.Equals(Normalize(snapshot.ApMac), ap, StringComparison.Ordinal)) continue;

                OverlayActiveLink(snapshot, measured);
                ApplyObservedBands(snapshot.Capabilities, measured.ObservedBands);
            }
        }

        return consoleClients;
    }

    /// <summary>
    /// Console snapshots by every MAC they can be reached under. An MLO client is keyed on its MLD
    /// MAC, matching the measured key, and its per-link MACs are added so a console that reports a
    /// link MAC as the client's identity still resolves to one snapshot.
    /// </summary>
    private static Dictionary<string, WirelessClientSnapshot> BuildIndex(List<WirelessClientSnapshot> clients)
    {
        var index = new Dictionary<string, WirelessClientSnapshot>(clients.Count, StringComparer.Ordinal);

        foreach (var client in clients)
        {
            if (!client.IsOnline) continue;
            index[Normalize(client.Mac)] = client;
        }

        foreach (var client in clients)
        {
            if (!client.IsOnline) continue;
            foreach (var link in client.MloLinks)
            {
                var mac = Normalize(link.Mac);
                if (mac.Length > 0) index.TryAdd(mac, client);
            }
        }

        return index;
    }

    /// <summary>
    /// The link scalars move as one group, because mixing a measured signal with a console channel
    /// would describe two different links. A console value only stands in where the measurement is
    /// absent AND both describe the same band.
    /// </summary>
    private static void OverlayActiveLink(WirelessClientSnapshot snapshot, MeasuredWirelessClient measured)
    {
        var sameBand = snapshot.Band == measured.Band;

        snapshot.Band = measured.Band;
        snapshot.Channel = measured.Channel ?? (sameBand ? snapshot.Channel : null);
        snapshot.ChannelWidth = measured.ChannelWidth ?? (sameBand ? snapshot.ChannelWidth : null);
        snapshot.Signal = measured.Signal;
        snapshot.Noise = measured.Noise ?? (sameBand ? snapshot.Noise : null);
        snapshot.Rssi = measured.Rssi ?? (sameBand ? snapshot.Rssi : null);
        snapshot.TxRate = measured.TxRate ?? (sameBand ? snapshot.TxRate : null);
        snapshot.RxRate = measured.RxRate ?? (sameBand ? snapshot.RxRate : null);

        if (measured.Satisfaction.HasValue) snapshot.Satisfaction = measured.Satisfaction;
    }

    /// <summary>
    /// Band support is asserted only from bands the client has actually been measured on. Nothing
    /// else is claimed: the series carries no capability bits, and a guess here decides whether
    /// Band Steering calls a client steerable.
    /// </summary>
    private static void ApplyObservedBands(ClientCapabilities capabilities, IReadOnlyCollection<RadioBand> bands)
    {
        foreach (var band in bands)
        {
            switch (band)
            {
                case RadioBand.Band2_4GHz: capabilities.Supports2_4GHz = true; break;
                case RadioBand.Band5GHz: capabilities.Supports5GHz = true; break;
                case RadioBand.Band6GHz: capabilities.Supports6GHz = true; break;
            }
        }
    }

    /// <summary>
    /// A run of empty buckets at or above this length is a collection gap worth reporting. One or
    /// two empty buckets is ordinary: a client that moved no traffic has no point written for it.
    /// Diagnosis only - the fill itself never waits for a run this long.
    /// </summary>
    public const int ReportableGapBuckets = 3;

    /// <summary>
    /// Merges AP-measured history over the console's own client report for the same range.
    ///
    /// Ours wins wherever we measured: a bucket we have takes its link values from the measurement,
    /// keeping the console-only fields (protocol, packet and retry counts) on the same point. A
    /// bucket we do not have keeps the console point untouched, which is the gap fill. Nothing is
    /// ever synthesized, so an absent value stays absent rather than becoming a measured zero.
    /// </summary>
    public static List<ClientWiFiMetrics> ApplyHistory(
        List<ClientWiFiMetrics> consoleMetrics,
        IReadOnlyList<MeasuredClientSample>? measured,
        TimeSpan bucket)
    {
        if (measured == null || measured.Count == 0) return consoleMetrics;
        if (bucket <= TimeSpan.Zero) return consoleMetrics;

        var byBucket = new Dictionary<long, ClientWiFiMetrics>(consoleMetrics.Count);
        foreach (var point in consoleMetrics)
            byBucket[BucketOf(point.Timestamp, bucket)] = point;

        var clientMac = consoleMetrics.Count > 0 ? consoleMetrics[0].ClientMac : string.Empty;

        foreach (var sample in measured)
        {
            var key = BucketOf(sample.Timestamp, bucket);
            if (byBucket.TryGetValue(key, out var point))
            {
                OverlayMetrics(point, sample);
                continue;
            }

            var added = new ClientWiFiMetrics { Timestamp = sample.Timestamp, ClientMac = clientMac };
            OverlayMetrics(added, sample);
            byBucket[key] = added;
            consoleMetrics.Add(added);
        }

        consoleMetrics.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return consoleMetrics;
    }

    /// <summary>
    /// How many buckets in the range the series did not cover, and the longest unbroken run of
    /// them. Reporting only - callers use it to say how much of an answer came from the console.
    /// </summary>
    public static (int Missing, int LongestRun) MeasureGaps(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<MeasuredClientSample> measured,
        TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero || end <= start) return (0, 0);

        var covered = new HashSet<long>();
        foreach (var sample in measured) covered.Add(BucketOf(sample.Timestamp, bucket));

        int missing = 0, run = 0, longest = 0;
        for (var key = BucketOf(start, bucket); key < end.ToUnixTimeMilliseconds(); key += (long)bucket.TotalMilliseconds)
        {
            if (covered.Contains(key))
            {
                run = 0;
                continue;
            }
            missing++;
            run++;
            if (run > longest) longest = run;
        }
        return (missing, longest);
    }

    /// <summary>
    /// The measured link values replace the console's. Fields the access point does not report are
    /// left alone, so a console-sourced protocol or packet count survives on the same point.
    /// </summary>
    private static void OverlayMetrics(ClientWiFiMetrics point, MeasuredClientSample sample)
    {
        if (!string.IsNullOrEmpty(sample.ApMac)) point.ApMac = sample.ApMac;
        if (sample.Band != RadioBand.Unknown) point.Band = sample.Band;
        if (sample.Channel.HasValue) point.Channel = sample.Channel;
        if (sample.ChannelWidth.HasValue) point.ChannelWidth = sample.ChannelWidth;
        if (sample.Signal.HasValue) point.Signal = sample.Signal;
        if (sample.TxRateKbps.HasValue) point.TxRateKbps = sample.TxRateKbps;
        if (sample.RxRateKbps.HasValue) point.RxRateKbps = sample.RxRateKbps;
        if (sample.Satisfaction.HasValue) point.Satisfaction = sample.Satisfaction;
    }

    private static long BucketOf(DateTimeOffset at, TimeSpan bucket)
    {
        var ms = (long)bucket.TotalMilliseconds;
        return at.ToUnixTimeMilliseconds() / ms * ms;
    }

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}

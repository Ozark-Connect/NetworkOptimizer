using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Turns wifi_client rows into the readings the Wi-Fi rule engine consumes.
///
/// Separate from the source that queries them so the judgment calls - which row is newest, which
/// source wrote it, whether it is still fresh - are testable without an InfluxDB.
/// </summary>
public static class MeasuredClientReducer
{
    /// <summary>Maps the measurement's band tag onto the band the rule engine works in.</summary>
    public static RadioBand BandFromTag(string? tag) => (tag ?? "").Trim().ToLowerInvariant() switch
    {
        "2.4ghz" => RadioBand.Band2_4GHz,
        "5ghz" => RadioBand.Band5GHz,
        "6ghz" => RadioBand.Band6GHz,
        _ => RadioBand.Unknown
    };

    /// <summary>
    /// Whether a row was written by the AP Agent path. The console's stat/sta reports none of these
    /// fields, which the write side states explicitly, so their presence is what tells the two
    /// sources apart on a measurement neither tags with its origin.
    /// </summary>
    public static bool IsAgentMeasured(MonitoringInfluxClient.WifiClientSamplePoint row)
        => row.TxRetries.HasValue || row.TxAttempts.HasValue || row.Ccq.HasValue
            || row.Nss.HasValue || row.LatencyAvgMs.HasValue;

    /// <summary>
    /// The newest AP-measured reading per client, grouped by access point, for the access points in
    /// <paramref name="coveredAps"/> only. A reading older than <paramref name="maxAge"/> is
    /// dropped: an agent that stopped writing hands its access point back to the console rather
    /// than holding it on a stale number.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<MeasuredWirelessClient>> Reduce(
        IReadOnlyList<MonitoringInfluxClient.WifiClientSamplePoint> rows,
        IReadOnlySet<string> coveredAps,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? observedBands,
        DateTime now,
        TimeSpan maxAge)
    {
        var newest = new Dictionary<string, MonitoringInfluxClient.WifiClientSamplePoint>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var clientMac = Normalize(row.ClientMac);
            var apMac = Normalize(row.ApMac);
            if (clientMac.Length == 0 || apMac.Length == 0) continue;
            if (!coveredAps.Contains(apMac)) continue;
            if (!IsAgentMeasured(row)) continue;
            if (now - row.Time > maxAge) continue;
            if (BandFromTag(row.Band) == RadioBand.Unknown) continue;

            if (!newest.TryGetValue(clientMac, out var held) || row.Time > held.Time)
                newest[clientMac] = row;
        }

        var byAp = new Dictionary<string, List<MeasuredWirelessClient>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clientMac, row) in newest)
        {
            var apMac = Normalize(row.ApMac);
            var client = new MeasuredWirelessClient
            {
                Mac = clientMac,
                ApMac = apMac,
                MeasuredAt = new DateTimeOffset(DateTime.SpecifyKind(row.Time, DateTimeKind.Utc)),
                Band = BandFromTag(row.Band),
                Channel = Positive(row.Channel),
                ChannelWidth = Positive(row.ChannelWidth),
                Signal = row.SignalDbm is { } signal ? (int)Math.Round(signal) : null,
                Noise = row.NoiseDbm is { } noise ? (int)Math.Round(noise) : null,
                Rssi = row.Rssi,
                TxRate = Positive(row.TxRateKbps),
                RxRate = Positive(row.RxRateKbps),
                Satisfaction = row.Satisfaction,
                ObservedBands = BandsFor(clientMac, observedBands),
            };

            if (!byAp.TryGetValue(apMac, out var list))
            {
                list = new List<MeasuredWirelessClient>();
                byAp[apMac] = list;
            }
            list.Add(client);
        }

        return byAp.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MeasuredWirelessClient>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One history sample per bucket. Rows arrive already bucketed by the query, so the newest row
    /// in a bucket wins and nothing is averaged here - averaging a channel would invent one.
    /// </summary>
    public static IReadOnlyList<MeasuredClientSample> ReduceHistory(
        IReadOnlyList<MonitoringInfluxClient.WifiClientSamplePoint> rows,
        TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero) return Array.Empty<MeasuredClientSample>();

        var ms = (long)bucket.TotalMilliseconds;
        var newest = new Dictionary<long, MonitoringInfluxClient.WifiClientSamplePoint>();

        foreach (var row in rows)
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(row.Time, DateTimeKind.Utc));
            var key = at.ToUnixTimeMilliseconds() / ms * ms;
            if (!newest.TryGetValue(key, out var held) || Outranks(row, held))
                newest[key] = row;
        }

        return newest
            .OrderBy(kv => kv.Key)
            .Select(kv => new MeasuredClientSample
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(kv.Key),
                ApMac = Normalize(kv.Value.ApMac) is { Length: > 0 } ap ? ap : null,
                Band = BandFromTag(kv.Value.Band),
                Channel = Positive(kv.Value.Channel),
                ChannelWidth = Positive(kv.Value.ChannelWidth),
                Signal = kv.Value.SignalDbm is { } signal ? (int)Math.Round(signal) : null,
                TxRateKbps = Positive(kv.Value.TxRateKbps),
                RxRateKbps = Positive(kv.Value.RxRateKbps),
                Satisfaction = kv.Value.Satisfaction,
            })
            .ToList();
    }

    private static IReadOnlyCollection<RadioBand> BandsFor(
        string clientMac,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? observedBands)
    {
        if (observedBands == null || !observedBands.TryGetValue(clientMac, out var tags))
            return Array.Empty<RadioBand>();

        var bands = new HashSet<RadioBand>();
        foreach (var tag in tags)
        {
            var band = BandFromTag(tag);
            if (band != RadioBand.Unknown) bands.Add(band);
        }
        return bands;
    }

    /// <summary>
    /// Which of two access points claiming a client in the same bucket is the one serving it.
    ///
    /// Recency alone answers "who wrote last", not "who has the client". An access point that still
    /// holds a station it stopped serving keeps writing, and on a walk test it interleaves weak
    /// readings between the strong ones from the access point the client is actually on - so a
    /// bucket it happens to write last in shows the wrong access point at the wrong signal.
    ///
    /// Idle decides where both report it, because it is the only field that says when the access
    /// point last heard from the client. Signal breaks a tie, and a point with no idle at all falls
    /// back to recency, which is what every point written before the field existed carries.
    /// </summary>
    private static bool Outranks(
        MonitoringInfluxClient.WifiClientSamplePoint row,
        MonitoringInfluxClient.WifiClientSamplePoint held)
    {
        if (row.IdleSeconds is { } rowIdle && held.IdleSeconds is { } heldIdle && rowIdle != heldIdle)
            return rowIdle < heldIdle;

        if (row.SignalDbm is { } rowSignal && held.SignalDbm is { } heldSignal
            && Math.Abs(rowSignal - heldSignal) >= ContestedSignalMarginDb)
            return rowSignal > heldSignal;

        return row.Time > held.Time;
    }

    /// <summary>
    /// How much stronger one claim must read before signal decides it. Wide enough that ordinary
    /// variation between two samples never reorders them, narrow enough to separate an access point
    /// hearing a client across the house from the one it is standing next to.
    /// </summary>
    private const double ContestedSignalMarginDb = 12;

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}

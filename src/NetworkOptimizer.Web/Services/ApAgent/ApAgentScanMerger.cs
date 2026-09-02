using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Lays what an AP Agent's radios hear over the console's scan results for the same access
/// point and band. Neighbors join the console's list (a BSSID both report keeps the stronger
/// signal and the fresher sighting); a spectrum table replaces the console's per-channel data for
/// that band, so the recommender's scan term and the stale-scan nudge read the AP's continuous
/// measurement instead of a scan cycle that may be hours old. An access point no agent covers,
/// or whose reading is stale, is left exactly as the console built it.
/// </summary>
public static class ApAgentScanMerger
{
    /// <summary>A reading older than this is not merged; the console's scan stands.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(5);

    /// <summary>Applies the merge in place. Returns how many (AP, band) results took agent data.</summary>
    public static int Apply(List<ChannelScanResult> results, Func<string, ApAgentScanPayload?> scanFor, DateTimeOffset now)
    {
        var merged = 0;
        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result.ApMac) || result.Band == RadioBand.Unknown) continue;
            var payload = scanFor(result.ApMac);
            if (payload == null) continue;
            var readAt = new DateTimeOffset(DateTime.SpecifyKind(payload.ReadAt, DateTimeKind.Utc));
            if (readAt == DateTimeOffset.UnixEpoch || now - readAt > FreshWindow) continue;

            var touched = MergeNeighbors(result, payload, readAt);
            touched |= MergeSpectrum(result, payload, readAt);
            if (touched) merged++;
        }
        return merged;
    }

    /// <summary>
    /// Every radio's sightings on this band, the scan radio's included: a serving radio hears its
    /// own channel, the scan radio the whole band, and the pool de-duplicates by BSSID.
    /// </summary>
    private static bool MergeNeighbors(ChannelScanResult result, ApAgentScanPayload payload, DateTimeOffset readAt)
    {
        var byBssid = result.Neighbors
            .Where(n => !string.IsNullOrEmpty(n.Bssid))
            .GroupBy(n => n.Bssid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var touched = false;
        foreach (var radio in payload.Radios)
        {
            foreach (var e in radio.Scan)
            {
                if (string.IsNullOrEmpty(e.Bssid) || BandOf(e.Band) != result.Band) continue;
                var seenAt = readAt.AddSeconds(-Math.Max(0, e.AgeSeconds));

                if (byBssid.TryGetValue(e.Bssid, out var existing))
                {
                    if (existing.Signal is not { } s || e.Signal > s) existing.Signal = e.Signal;
                    if (existing.LastSeen is not { } l || seenAt > l) existing.LastSeen = seenAt;
                    if (existing.Width is null && e.Width > 0) existing.Width = e.Width;
                    touched = true;
                    continue;
                }

                var added = new NeighborNetwork
                {
                    Ssid = e.Essid ?? string.Empty,
                    Bssid = e.Bssid.ToLowerInvariant(),
                    Channel = e.Channel,
                    Width = e.Width > 0 ? e.Width : null,
                    Signal = e.Signal,
                    IsOwnNetwork = e.IsUbnt,
                    LastSeen = seenAt,
                };
                result.Neighbors.Add(added);
                byBssid[added.Bssid] = added;
                touched = true;
            }
        }
        return touched;
    }

    /// <summary>
    /// The serving radio's own spectrum table for the band, else the scan radio's channels that
    /// fall in it. Replaces the console's channels wholesale: mixing two scans' channels would
    /// date one band with two clocks.
    /// </summary>
    private static bool MergeSpectrum(ChannelScanResult result, ApAgentScanPayload payload, DateTimeOffset readAt)
    {
        var serving = payload.Radios.FirstOrDefault(r => !r.ScanRadio && BandOf(r.Band) == result.Band && r.Spectrum.Count > 0);
        var scanRadio = payload.Radios.FirstOrDefault(r => r.ScanRadio && r.Spectrum.Any(s => BandOfMhz(s.CenterMhz) == result.Band));
        var source = serving ?? scanRadio;
        if (source == null) return false;

        var entries = source.Spectrum.Where(s => serving != null || BandOfMhz(s.CenterMhz) == result.Band).ToList();
        if (entries.Count == 0) return false;

        result.Channels = entries.Select(s => new ChannelInfo
        {
            Channel = s.Channel,
            Width = s.Width > 0 ? s.Width : null,
            CenterFrequency = s.CenterMhz > 0 ? s.CenterMhz : null,
            Utilization = Math.Clamp(s.Utilization, 0, 100),
            // Same convention as the console's scan: "interference" is a dBm floor, not a percent.
            NoiseFloor = s.Interference < 0 ? s.Interference : null,
            NeighborCount = s.OtherBssCount,
        }).ToList();
        result.SpectrumTableTime = source.SpectrumAt is { } at
            ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
            : readAt;
        return true;
    }

    /// <summary>The agent's band token, as the Go side's bandForRadio emits it.</summary>
    private static RadioBand BandOf(string? token) => (token ?? "").Trim() switch
    {
        "2.4" => RadioBand.Band2_4GHz,
        "5" => RadioBand.Band5GHz,
        "6" => RadioBand.Band6GHz,
        _ => RadioBand.Unknown
    };

    private static RadioBand BandOfMhz(int mhz) => mhz switch
    {
        >= 2400 and < 2500 => RadioBand.Band2_4GHz,
        >= 5000 and < 5925 => RadioBand.Band5GHz,
        >= 5925 and < 7200 => RadioBand.Band6GHz,
        _ => RadioBand.Unknown
    };
}

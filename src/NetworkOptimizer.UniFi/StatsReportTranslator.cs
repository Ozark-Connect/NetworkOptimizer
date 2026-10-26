using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi;

/// <summary>The legacy stat/report object a stats query stands in for.</summary>
public enum StatsReportKind
{
    /// <summary>stat/report/{granularity}.site, attrs prefixed "ap-{band}-".</summary>
    Site,
    /// <summary>stat/report/{granularity}.ap, attrs prefixed "{band}-".</summary>
    Ap,
    /// <summary>stat/report/{granularity}.user, one client.</summary>
    User,
}

/// <summary>
/// Rebuilds legacy stat/report rows from the Network 11 stats API (v2/api/stats/recent/series), so
/// every report caller parses one shape whichever API the console serves. Only requested attrs are
/// written, and an attr with no stats equivalent is left out, as stat/report leaves out an attr it lacks.
/// </summary>
[VendorSpecific("UniFi", "Network 11 replaced stat/report with v2/api/stats; schema names and fields are the console's")]
public static class StatsReportTranslator
{
    /// <summary>The stats schema each legacy report reads.</summary>
    public static string Schema(StatsReportKind kind) => kind switch
    {
        StatsReportKind.Site => "wirelessBySite",
        StatsReportKind.Ap => "wirelessBySiteByDevice",
        _ => "activityBySiteByClient",
    };

    // Legacy per-band radio attr suffix -> stats field. Both are the AP's view of its radio.
    private static readonly Dictionary<string, string> RadioFields = new()
    {
        ["cu_total"] = "avgTotalCuPct",
        ["cu_interf"] = "avgInterfCuPct",
        ["tx_retries"] = "wifiTxRetryPackets",
        ["wifi_tx_attempts"] = "wifiTxAttemptPackets",
        ["wifi_tx_dropped"] = "wifiTxDroppedPackets",
        ["tx_packets"] = "txPackets",
        ["rx_packets"] = "rxPackets",
    };

    // Legacy client attr -> stats field. The client schema is in the client's view, so its rx is the
    // AP's tx: retries, attempts, and packets swap direction to keep the legacy (AP-side) meaning.
    private static readonly Dictionary<string, string> ClientFields = new()
    {
        ["signal"] = "avgSignalDbm",
        ["satisfaction"] = "avgSatisfactionScore",
        ["tx_retries"] = "wifiRxRetryPackets",
        ["wifi_tx_attempts"] = "wifiRxAttemptPackets",
        ["wifi_tx_dropped"] = "wifiRxDroppedPackets",
        ["tx_packets"] = "rxPackets",
        ["rx_packets"] = "txPackets",
        ["tx_bytes"] = "rxBytes",
        ["rx_bytes"] = "txBytes",
        ["tx_rate"] = "mostCommonRxRateTierKbps",
        ["radio_protocol_most_common"] = "mostCommonRadioProtocol",
    };

    /// <summary>The bucket a legacy report granularity names; null for 5minutes, which the console never exceeds.</summary>
    public static TimeSpan? Bucket(string granularity) => granularity switch
    {
        "hourly" => TimeSpan.FromHours(1),
        "daily" => TimeSpan.FromDays(1),
        _ => null,
    };

    /// <summary>
    /// Translates a stats series response into legacy report rows. Rows for another site are dropped,
    /// since the stats API is console-wide. The console picks its own bucket from the range, so points
    /// finer than <paramref name="bucket"/> are rolled up: callers count rows as hours or days.
    /// </summary>
    public static JsonElement ToLegacyRows(JsonElement stats, StatsReportKind kind, string? siteId, IReadOnlyCollection<string> attrs, TimeSpan? bucket = null)
    {
        var rows = new JsonArray();
        if (stats.ValueKind == JsonValueKind.Array)
        {
            foreach (var entity in stats.EnumerateArray())
            {
                if (siteId != null && entity.TryGetProperty("siteId", out var sid) && sid.ValueKind == JsonValueKind.String
                    && !string.Equals(sid.GetString(), siteId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!entity.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Array) continue;
                var apMac = entity.TryGetProperty("deviceMac", out var dm) ? dm.GetString() : null;
                foreach (var point in RollUp(series, bucket))
                {
                    if (!point.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Number) continue;
                    var row = new JsonObject { ["time"] = ts.GetInt64() };
                    if (kind == StatsReportKind.User) WriteClient(row, point, attrs);
                    else WriteRadio(row, point, kind == StatsReportKind.Site ? "ap-" : "", attrs);
                    if (kind == StatsReportKind.Ap && apMac != null) row["ap"] = apMac;
                    rows.Add(row);
                }
            }
        }
        using var doc = JsonDocument.Parse(rows.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void WriteRadio(JsonObject row, JsonElement point, string prefix, IReadOnlyCollection<string> attrs)
    {
        foreach (var band in Nested(point, "byRadioBand"))
        {
            var code = band.TryGetProperty("radioBand", out var rb) ? rb.GetString() : null;
            if (code == null) continue;
            foreach (var (suffix, field) in RadioFields)
            {
                var key = $"{prefix}{code}-{suffix}";
                if (attrs.Contains(key) && Value(band, field) is { } v) row[key] = v;
            }
        }
    }

    private static void WriteClient(JsonObject row, JsonElement point, IReadOnlyCollection<string> attrs)
    {
        foreach (var (attr, field) in ClientFields)
            if (attrs.Contains(attr) && Value(point, field) is { } v) row[attr] = v;

        foreach (var band in Nested(point, "byRadioBand"))
            if (band.TryGetProperty("radioBand", out var rb) && $"{rb.GetString()}-signal" is var key
                && attrs.Contains(key) && Value(band, "avgSignalDbm") is { } v)
                row[key] = v;

        // Most-connected AP first: callers take the first as the client's AP.
        var devices = Nested(point, "byDevice")
            .Where(d => d.TryGetProperty("deviceMac", out var m) && m.ValueKind == JsonValueKind.String)
            .OrderByDescending(d => d.TryGetProperty("durationMs", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : 0)
            .ToList();
        if (attrs.Contains("x-set-ap_macs"))
            row["x-set-ap_macs"] = new JsonArray(devices.Select(d => (JsonNode?)d.GetProperty("deviceMac").GetString()).ToArray());
        if (attrs.Contains("duration_map-ap_duration"))
        {
            var map = new JsonObject();
            foreach (var d in devices)
                if (Value(d, "durationMs") is { } ms) map[d.GetProperty("deviceMac").GetString()!] = ms;
            row["duration_map-ap_duration"] = map;
        }
        foreach (var d in devices)
        {
            var mac = d.GetProperty("deviceMac").GetString();
            foreach (var band in Nested(d, "byRadioBand"))
            {
                var key = $"{(band.TryGetProperty("radioBand", out var rb) ? rb.GetString() : "")}-{mac}-channel_info_most_common";
                if (attrs.Contains(key) && Value(band, "mostCommonChannelDescriptor") is { } ch) row[key] = ch;
            }
        }
    }

    // Fields that identify a nested entry, so entries for the same band or AP merge across points.
    private static readonly string[] KeyFields =
        ["radioBand", "deviceMac", "radioName", "wlanConfigurationId", "wifiStandard", "signalCategory", "networkGroup", "wlanId"];

    /// <summary>Merges points into buckets aligned to the epoch; points already as coarse pass through.</summary>
    private static IEnumerable<JsonElement> RollUp(JsonElement series, TimeSpan? bucket)
    {
        var points = series.EnumerateArray()
            .Where(p => p.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number)
            .ToList();
        if (bucket is not { } b || points.All(p => Number(p, "duration") is not { } d || d >= b.TotalMilliseconds))
            return points;

        var size = (long)b.TotalMilliseconds;
        return points
            .GroupBy(p => p.GetProperty("timestamp").GetInt64() / size * size)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var merged = Merge(g.Select(p => (p, Number(p, "duration") ?? 1)).ToList());
                merged["timestamp"] = g.Key;
                using var doc = JsonDocument.Parse(merged.ToJsonString());
                return doc.RootElement.Clone();
            })
            .ToList();
    }

    /// <summary>
    /// One entry from several of the same entity, each weighted by its point's duration: counts and
    /// durations sum, max/min and distinct counts take the extreme, other numbers a weighted mean.
    /// </summary>
    private static JsonObject Merge(List<(JsonElement El, double Weight)> parts)
    {
        var result = new JsonObject();
        foreach (var name in parts.SelectMany(p => p.El.EnumerateObject().Select(o => o.Name)).Distinct())
        {
            var values = parts.Where(p => p.El.TryGetProperty(name, out _)).Select(p => (V: p.El.GetProperty(name), p.Weight)).ToList();
            var first = values[0].V;
            if (first.ValueKind == JsonValueKind.Number)
            {
                var nums = values.Where(v => v.V.ValueKind == JsonValueKind.Number).Select(v => (N: v.V.GetDouble(), v.Weight)).ToList();
                result[name] = name switch
                {
                    _ when name.StartsWith("duration", StringComparison.Ordinal) || name.EndsWith("DurationMs", StringComparison.Ordinal)
                        || name.Contains("Packets", StringComparison.Ordinal) || name.Contains("Bytes", StringComparison.Ordinal)
                        || name.EndsWith("Failures", StringComparison.Ordinal) || name.EndsWith("Stalls", StringComparison.Ordinal)
                        => nums.Sum(n => n.N),
                    _ when name.StartsWith("max", StringComparison.Ordinal) || name.StartsWith("distinct", StringComparison.Ordinal)
                        => nums.Max(n => n.N),
                    _ when name.StartsWith("min", StringComparison.Ordinal) => nums.Min(n => n.N),
                    _ => nums.Sum(n => n.Weight) is var w && w > 0 ? nums.Sum(n => n.N * n.Weight) / w : nums.Average(n => n.N),
                };
            }
            else if (first.ValueKind == JsonValueKind.Array && first.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object))
            {
                var merged = new JsonArray();
                var groups = values
                    .SelectMany(v => v.V.EnumerateArray().Select(e => (El: e, v.Weight)))
                    .GroupBy(e => string.Join("|", KeyFields.Select(k => e.El.TryGetProperty(k, out var kv) ? kv.ToString() : "")));
                foreach (var group in groups) merged.Add(Merge(group.ToList()));
                result[name] = merged;
            }
            else
            {
                var kept = values.Select(v => v.V).Where(v => v.ValueKind != JsonValueKind.Null).ToList();
                result[name] = kept.Count > 0 ? JsonNode.Parse(kept[0].GetRawText()) : null;
            }
        }
        return result;
    }

    private static double? Number(JsonElement el, string field) =>
        el.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static IEnumerable<JsonElement> Nested(JsonElement el, string name) =>
        el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : [];

    private static JsonNode? Value(JsonElement el, string field) =>
        !el.TryGetProperty(field, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.Number => JsonValue.Create(v.GetDouble()),
            JsonValueKind.String => JsonValue.Create(v.GetString()),
            _ => null,
        };
}

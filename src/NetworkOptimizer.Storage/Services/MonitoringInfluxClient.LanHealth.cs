namespace NetworkOptimizer.Storage.Services;

/// <summary>LAN Health reads. Each is bounded to one window the caller sizes (the roll-up asks for one hour).</summary>
public partial class MonitoringInfluxClient
{
    /// <summary>One port series' field over a window: first, last, min, max, mean.</summary>
    public sealed record PortFieldStats(
        DateTime WindowStart, string DeviceMac, string IfName, string PortId, string Field,
        double? First, double? Last, double? Min, double? Max, double? Mean);

    /// <summary>A LAN Health event from <c>events</c>.</summary>
    public sealed record LanEventPoint(
        DateTime Time, string DeviceMac, string EventType, string? IfName, string? OldValue, string? NewValue, string? Detail);

    /// <summary>One fabric probe sample with loss.</summary>
    public sealed record ProbeLossPoint(DateTime Time, string TargetId, double LossPercent);

    /// <summary>
    /// Every interface_counters field LAN Health reads, per series, aggregated over
    /// <paramref name="window"/> (stamped at the window's start). Four selector pushdowns (first, last,
    /// min, max) plus a mean over the rate fields. Callers keep the range to an hour or two.
    /// </summary>
    public async Task<IReadOnlyList<PortFieldStats>> QueryLanPortStatsAsync(
        DateTime from, DateTime to, TimeSpan window, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        if (!IsConfigured || fields.Count == 0) return Array.Empty<PortFieldStats>();
        var fieldFilter = string.Join(" or ", fields.Select(f => $"r._field == {FluxLiteral.Quote(f)}"));
        async Task<Dictionary<(DateTime, string, string, string, string), double>> Agg(string fn, string? onlyFields = null)
        {
            var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"")
  |> filter(fn: (r) => {onlyFields ?? fieldFilter})
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: {fn}, createEmpty: false, timeSrc: ""_start"")
";
            var values = new Dictionary<(DateTime, string, string, string, string), double>();
            await foreach (var record in QueryFluxAsync(flux, ct))
            {
                var key = (ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                    record.GetValueByKey("device_mac") as string ?? "",
                    record.GetValueByKey("if_name") as string ?? "",
                    record.GetValueByKey("port_id") as string ?? "",
                    record.GetField() ?? "");
                var v = record.GetValue() switch { bool b => b ? 1.0 : 0.0, var o => AsDoubleOrNull(o) };
                if (v is double d) values[key] = d;
            }
            return values;
        }
        // oper_status's mean is the share of the hour a port spent down: LAN Health's sleep check reads it.
        var rateFields = fields.Where(f => f is "rate_in_bps" or "rate_out_bps" or "oper_status").ToList();
        var first = Agg("first");
        var last = Agg("last");
        var min = Agg("min");
        var max = Agg("max");
        var mean = rateFields.Count > 0
            ? Agg("mean", string.Join(" or ", rateFields.Select(f => $"r._field == {FluxLiteral.Quote(f)}")))
            : Task.FromResult(new Dictionary<(DateTime, string, string, string, string), double>());
        await Task.WhenAll(first, last, min, max, mean);
        return last.Result.Keys.Union(first.Result.Keys)
            .Select(k => new PortFieldStats(k.Item1, k.Item2, k.Item3, k.Item4, k.Item5,
                first.Result.TryGetValue(k, out var f) ? f : null,
                last.Result.TryGetValue(k, out var l) ? l : null,
                min.Result.TryGetValue(k, out var mn) ? mn : null,
                max.Result.TryGetValue(k, out var mx) ? mx : null,
                mean.Result.TryGetValue(k, out var me) ? me : null))
            .ToList();
    }

    /// <summary>One console port series' PoE draw at an instant.</summary>
    public sealed record PortPoePoint(string DeviceMac, string IfName, string PortId, double Watts);

    /// <summary>
    /// Each console port series' <c>poe_power</c> nearest <paramref name="at"/>, from the five minutes before it
    /// to 30 s after: Port Statistics' PoE column on a historic scrub. A port whose sample has <c>poe_mode</c> "off"
    /// (written in the same point) is left out, so it reads as no PoE.
    /// </summary>
    public async Task<IReadOnlyList<PortPoePoint>> QueryPortPoeAtAsync(DateTime at, CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<PortPoePoint>();
        var center = at.ToUniversalTime();
        var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(center.AddMinutes(-5))}, stop: {ToFluxInstant(center.AddSeconds(30))})
  |> filter(fn: (r) => r._measurement == ""interface_counters"" and (r._field == {FluxLiteral.Quote(InfluxFieldNames.PoePower)} or r._field == {FluxLiteral.Quote(InfluxFieldNames.PoeMode)}))
";
        var watts = new List<(DateTime Time, PortPoePoint Point)>();
        var off = new HashSet<(string Mac, string PortId, DateTime Time)>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var time = ToUtc(record.GetTimeInDateTime() ?? center);
            var mac = (record.GetValueByKey("device_mac") as string ?? "").ToLowerInvariant();
            var portId = record.GetValueByKey("port_id") as string ?? "";
            if (record.GetField() == InfluxFieldNames.PoeMode)
            {
                if (string.Equals(record.GetValue() as string, "off", StringComparison.OrdinalIgnoreCase)) off.Add((mac, portId, time));
                continue;
            }
            if (AsDoubleOrNull(record.GetValue()) is not double w) continue;
            watts.Add((time, new PortPoePoint(mac, record.GetValueByKey("if_name") as string ?? "", portId, w)));
        }
        return watts
            .GroupBy(s => (s.Point.DeviceMac, s.Point.PortId))
            .Select(g => g.OrderBy(s => Math.Abs((s.Time - center).Ticks)).First())
            .Where(s => !off.Contains((s.Point.DeviceMac, s.Point.PortId, s.Time)))
            .Select(s => s.Point)
            .ToList();
    }

    /// <summary>LAN Health events (link changes, uplink context, device states) in a window. Raw; <c>events</c> is sparse.</summary>
    public async Task<IReadOnlyList<LanEventPoint>> QueryLanEventsAsync(
        DateTime from, DateTime to, IReadOnlyList<string> eventTypes, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrEmpty(_longtermBucket) || eventTypes.Count == 0) return Array.Empty<LanEventPoint>();
        var types = string.Join(" or ", eventTypes.Select(t => $"r.event_type == {FluxLiteral.Quote(t)}"));
        var flux = $@"
from(bucket: {FluxLiteral.Quote(_longtermBucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""events"")
  |> filter(fn: (r) => {types})
  |> pivot(rowKey:[""_time""], columnKey: [""_field""], valueColumn: ""_value"")
";
        var results = new List<LanEventPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            results.Add(new LanEventPoint(
                ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow),
                record.GetValueByKey("device_mac") as string ?? "",
                record.GetValueByKey("event_type") as string ?? "",
                record.GetValueByKey("if_name") as string,
                record.GetValueByKey("old_value") as string,
                record.GetValueByKey("new_value") as string,
                record.GetValueByKey("detail") as string));
        }
        return results.OrderBy(r => r.Time).ToList();
    }

    /// <summary>Fabric probe samples that lost packets, raw. Loss is rare on a healthy LAN, so this stays small.</summary>
    public async Task<IReadOnlyList<ProbeLossPoint>> QueryFabricLossSamplesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<ProbeLossPoint>();
        var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"" and r.target_type == ""fabric"" and r._field == ""loss_percent"")
  |> filter(fn: (r) => r._value > 0.0)
";
        var results = new List<ProbeLossPoint>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            if (record.GetValueByKey("target_id") is not string id) continue;
            if (AsDoubleOrNull(record.GetValue()) is not double loss) continue;
            results.Add(new ProbeLossPoint(ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow), id, loss));
        }
        return results.OrderBy(r => r.Time).ToList();
    }

    /// <summary>
    /// Fabric probe sample counts, mean loss, and median RTT per target over a window, for coverage, the Links
    /// table's Probe Loss, and mesh RTT. Counts add across vantages; loss and RTT average across them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (int Samples, double MeanLoss, double? RttMs)>> QueryFabricProbeSummaryAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!IsConfigured) return new Dictionary<string, (int, double, double?)>();
        async Task<Dictionary<string, List<double>>> Agg(string field, string fn)
        {
            var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""latency"" and r.target_type == ""fabric"" and r._field == {FluxLiteral.Quote(field)})
  |> {fn}
";
            var map = new Dictionary<string, List<double>>();
            await foreach (var record in QueryFluxAsync(flux, ct))
                if (record.GetValueByKey("target_id") is string id && AsDoubleOrNull(record.GetValue()) is double v)
                {
                    if (!map.TryGetValue(id, out var list)) map[id] = list = [];
                    list.Add(v);
                }
            return map;
        }
        var count = Agg("loss_percent", "count()");
        var mean = Agg("loss_percent", "mean()");
        var rtt = Agg("rtt_avg_ms", "median()");
        await Task.WhenAll(count, mean, rtt);
        return count.Result.ToDictionary(kv => kv.Key, kv => ((int)kv.Value.Sum(),
            mean.Result.TryGetValue(kv.Key, out var m) ? m.Average() : 0,
            rtt.Result.TryGetValue(kv.Key, out var r) ? r.Average() : (double?)null));
    }

    /// <summary>Each SFP module's best (highest) receive power since <paramref name="from"/>, by device MAC and port, from the longterm <c>sfp</c> series.</summary>
    public async Task<IReadOnlyDictionary<(string DeviceMac, string Port), double>> QuerySfpRxBestAsync(DateTime from, CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string), double>();
        if (!IsConfigured || string.IsNullOrEmpty(_longtermBucket)) return result;
        var flux = $@"
from(bucket: {FluxLiteral.Quote(_longtermBucket)})
  |> range(start: {ToFluxInstant(from)})
  |> filter(fn: (r) => r._measurement == ""sfp"" and r._field == ""rx_power_dbm"")
  |> max()
";
        await foreach (var record in QueryFluxAsync(flux, ct))
            if (record.GetValueByKey("device_mac") is string mac && record.GetValueByKey("port_name") is string port && AsDoubleOrNull(record.GetValue()) is double v)
                result[(NormalizeMac(mac), port)] = v;
        return result;
    }

    /// <summary>
    /// Device-level LAN Health fields from <c>device_health</c> (mesh uplink, PoE totals) per window: mean, first,
    /// and last, with the time the last reading was taken (<c>LastAt</c>), which is not the window's end.
    /// </summary>
    public async Task<IReadOnlyList<(DateTime WindowStart, string DeviceMac, string Field, double? Mean, object? Last, double? First, DateTime? LastAt)>> QueryLanDeviceFieldsAsync(
        DateTime from, DateTime to, TimeSpan window, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        if (!IsConfigured || fields.Count == 0) return Array.Empty<(DateTime, string, string, double?, object?, double?, DateTime?)>();
        var fieldFilter = string.Join(" or ", fields.Select(f => $"r._field == {FluxLiteral.Quote(f)}"));
        async Task<Dictionary<(DateTime, string, string), object?>> Agg(string fn, bool numericOnly)
        {
            var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""device_health"")
  |> filter(fn: (r) => {fieldFilter})
  {(numericOnly ? "|> filter(fn: (r) => r._field != \"uplink_parent_mac\" and r._field != \"poe_near_limit\" and r._field !~ /_radio$/)" : "")}
  |> aggregateWindow(every: {ToFluxDuration(window)}, fn: {fn}, createEmpty: false, timeSrc: ""_start"")
";
            var map = new Dictionary<(DateTime, string, string), object?>();
            await foreach (var record in QueryFluxAsync(flux, ct))
            {
                var key = (ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow), record.GetValueByKey("device_mac") as string ?? "", record.GetField() ?? "");
                map[key] = record.GetValue();
            }
            return map;
        }
        // Last keeps its own _time (window + last, not aggregateWindow), keyed by the window's start.
        async Task<Dictionary<(DateTime, string, string), (object? Value, DateTime At)>> Last()
        {
            var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""device_health"")
  |> filter(fn: (r) => {fieldFilter})
  |> window(every: {ToFluxDuration(window)})
  |> last()
";
            var map = new Dictionary<(DateTime, string, string), (object?, DateTime)>();
            await foreach (var record in QueryFluxAsync(flux, ct))
            {
                var at = ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow);
                var start = record.GetStart() is { } s ? s.ToDateTimeUtc() : at;
                map[(start, record.GetValueByKey("device_mac") as string ?? "", record.GetField() ?? "")] = (record.GetValue(), at);
            }
            return map;
        }
        var mean = Agg("mean", true);
        var last = Last();
        var first = Agg("first", true);
        await Task.WhenAll(mean, last, first);
        return last.Result.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Key.Item3,
                mean.Result.TryGetValue(kv.Key, out var m) ? AsDoubleOrNull(m) : null, kv.Value.Value,
                first.Result.TryGetValue(kv.Key, out var f) ? AsDoubleOrNull(f) : null, (DateTime?)kv.Value.At))
            .ToList();
    }

    /// <summary>Raw readings of a few fields on one device's ports, for a link's timeline. Bounded by the caller to one link and a few days.</summary>
    public async Task<IReadOnlyList<(DateTime Time, string IfName, string PortId, string Field, double Value)>> QueryLanPortRawAsync(
        string deviceMac, DateTime from, DateTime to, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        if (!IsConfigured || fields.Count == 0) return Array.Empty<(DateTime, string, string, string, double)>();
        var fieldFilter = string.Join(" or ", fields.Select(f => $"r._field == {FluxLiteral.Quote(f)}"));
        var flux = $@"
from(bucket: {FluxLiteral.Quote(_bucket)})
  |> range(start: {ToFluxInstant(from)}, stop: {ToFluxInstant(to)})
  |> filter(fn: (r) => r._measurement == ""interface_counters"" and r.device_mac == {FluxLiteral.Quote(NormalizeMac(deviceMac))})
  |> filter(fn: (r) => {fieldFilter})
";
        var results = new List<(DateTime, string, string, string, double)>();
        await foreach (var record in QueryFluxAsync(flux, ct))
        {
            var v = record.GetValue() switch { bool b => b ? 1.0 : 0.0, var o => AsDoubleOrNull(o) };
            if (v is not double d) continue;
            results.Add((ToUtc(record.GetTimeInDateTime() ?? DateTime.UtcNow), record.GetValueByKey("if_name") as string ?? "",
                record.GetValueByKey("port_id") as string ?? "", record.GetField() ?? "", d));
        }
        return results.OrderBy(r => r.Item1).ToList();
    }
}

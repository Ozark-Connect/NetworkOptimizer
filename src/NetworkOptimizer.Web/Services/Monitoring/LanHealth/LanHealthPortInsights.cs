using System.Text.Json;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Port Insights as one more source: its anomaly history, read slowly and stored as
/// <c>port_anomaly</c> events so LAN Health can count what UniFi itself noticed. Its history has
/// been empty on every test site, which the coverage view then says ("0 seen").
/// </summary>
public static class LanHealthPortInsights
{
    public const string EventType = "port_anomaly";

    /// <summary>One anomaly UniFi recorded on a port.</summary>
    public sealed record Anomaly(string DeviceMac, int PortIdx, DateTime At, string Kind);

    /// <summary>Reads the anomaly list tolerantly: an array, or one under "data"; field names vary by release.</summary>
    [VendorSpecific("UniFi", "v2 ports/port-anomalies; shape unverified (empty on every site seen)")]
    public static List<Anomaly> Parse(JsonElement root)
    {
        var list = new List<Anomaly>();
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : default;
        if (items.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var mac = Str(item, "device_mac") ?? Str(item, "mac") ?? Str(item, "deviceMac");
            var port = Int(item, "port_idx") ?? Int(item, "portIdx") ?? Int(item, "port");
            var at = Long(item, "timestamp") ?? Long(item, "time") ?? Long(item, "datetime");
            if (mac == null || port == null || at == null) continue;
            var when = at > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(at.Value) : DateTimeOffset.FromUnixTimeSeconds(at.Value);
            list.Add(new Anomaly(LanLinkBuilder.Mac(mac), port.Value, when.UtcDateTime, Str(item, "anomaly") ?? Str(item, "type") ?? "anomaly"));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
}

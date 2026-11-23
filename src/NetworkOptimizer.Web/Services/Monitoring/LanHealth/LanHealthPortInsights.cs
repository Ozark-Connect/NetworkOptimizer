using System.Text.Json;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Port Insights as one more source: UniFi's current anomaly scores per port, stored as <c>port_anomaly</c> events
/// when an anomaly appears so LAN Health can count what UniFi itself noticed.
/// </summary>
public static class LanHealthPortInsights
{
    public const string EventType = "port_anomaly";

    /// <summary>One anomaly UniFi scores on a port now: its category (CABLE_POWER, ...) and score.</summary>
    public sealed record Anomaly(string DeviceMac, int PortIdx, string Kind, int? Score);

    /// <summary>
    /// Reads <c>v2 ports/port-anomalies</c>: an array of devices, each with <c>port_anomalies</c> holding a port and
    /// its scores (captured from a UniFi Network 11.1 console, 2026-11-23). Scores carry no time: they are the
    /// current state, so the caller stamps a new one with its poll time.
    /// </summary>
    [VendorSpecific("UniFi", "v2 ports/port-anomalies: [{ mac, port_anomalies: [{ port_idx, score: [{ anomaly_category, anomaly_score }] }] }]")]
    public static List<Anomaly> Parse(JsonElement root)
    {
        var list = new List<Anomaly>();
        var devices = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : default;
        if (devices.ValueKind != JsonValueKind.Array) return list;
        foreach (var device in devices.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object || Str(device, "mac") is not { } mac) continue;
            if (!device.TryGetProperty("port_anomalies", out var ports) || ports.ValueKind != JsonValueKind.Array) continue;
            foreach (var port in ports.EnumerateArray())
            {
                if (port.ValueKind != JsonValueKind.Object || Int(port, "port_idx") is not int idx) continue;
                if (!port.TryGetProperty("score", out var scores) || scores.ValueKind != JsonValueKind.Array) continue;
                foreach (var s in scores.EnumerateArray())
                    if (s.ValueKind == JsonValueKind.Object && Str(s, "anomaly_category") is { } category)
                        list.Add(new Anomaly(LanLinkBuilder.Mac(mac), idx, category, Int(s, "anomaly_score")));
            }
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
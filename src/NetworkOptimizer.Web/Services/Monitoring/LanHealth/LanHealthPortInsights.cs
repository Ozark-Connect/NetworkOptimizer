using System.Text.Json;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Port Anomalies as one more source: UniFi's current anomaly scores per port, stored as <c>port_anomaly</c> events
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

    /// <summary>
    /// A note on each link whose port UniFi flags now, at either end. A disabled port is left out: it carries no
    /// traffic, so a flag there is lab work on an unused port, not the link's health. A link LAN Health already
    /// finds a fault on gets no note.
    /// </summary>
    public static List<LinkFinding> Notes(IReadOnlyList<LinkHealth> links, IReadOnlyList<Anomaly> anomalies,
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiDeviceResponse> devices)
    {
        var enabled = devices.Where(d => !string.IsNullOrEmpty(d.Mac))
            .SelectMany(d => (d.PortTable ?? []).Select(p => (Key: (LanLinkBuilder.Mac(d.Mac), p.PortIdx), p.Enable)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Enable);
        var notes = new List<LinkFinding>();
        foreach (var l in links)
        {
            var hits = anomalies.Where(a => At(a, l.Link.Upstream) || At(a, l.Link.Downstream)).ToList();
            // Our own fault finding on the link wins: UniFi's flag is then the same problem, seen by one more source.
            if (hits.Count == 0 || l.Findings.Any(f => Detectors.Scores(f.Detector))) continue;
            var text = string.Join(", ", hits.Select(a => $"{a.Kind.Replace('_', ' ').ToLowerInvariant()}{(a.Score is int s ? $" (score {s})" : "")}"));
            notes.Add(new LinkFinding
            {
                Detector = Detectors.PortInsights,
                LinkId = l.Link.Id,
                Severity = FindingSeverity.Info,
                Class = "Port Anomalies",
                Title = $"Port Anomalies: {l.Link.Name}",
                Body = $"UniFi Network flags this port: {text}.",
            });
        }
        return notes;

        bool At(Anomaly a, LinkEnd end) => end.PortIdx == a.PortIdx && end.DeviceMac == a.DeviceMac
            && enabled.GetValueOrDefault((a.DeviceMac, a.PortIdx), true);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
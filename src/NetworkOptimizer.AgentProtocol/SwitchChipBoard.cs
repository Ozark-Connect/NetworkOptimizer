using System.Text.Json;

namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Reads a UniFi gateway's switch chip port map from its own UniFi OS board file
/// (/usr/share/ubios-udapi-server/config-board/&lt;board&gt;-&lt;sysid&gt;.json). The chip port to ethN order differs by
/// board (reversed, in order, offset), so it is never derived from the port number.
/// </summary>
public static class SwitchChipBoard
{
    /// <summary>This board's file among <paramref name="files"/>: board.sysid=0xa67a names udr7-a67a.json. Null when none matches.</summary>
    public static string? BoardFile(string boardInfo, IEnumerable<string> files)
    {
        var sysid = boardInfo.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("board.sysid=", StringComparison.Ordinal))?["board.sysid=".Length..].Trim();
        if (string.IsNullOrEmpty(sysid)) return null;
        var suffix = "-" + (sysid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? sysid[2..] : sysid).ToLowerInvariant() + ".json";
        return files.FirstOrDefault(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// switch0's edge ports, chip port to ethN, kept only for ports swconfig lists. Null when the file has no
    /// switch0, names no CPU port matching the one swconfig reports, or maps no port.
    /// </summary>
    public static Dictionary<int, string>? PortMap(string boardJson, int swconfigCpu, IReadOnlyCollection<int> swconfigPorts)
    {
        try
        {
            using var doc = JsonDocument.Parse(boardJson);
            if (!doc.RootElement.TryGetProperty("switches", out var switches) || switches.ValueKind != JsonValueKind.Array) return null;
            foreach (var sw in switches.EnumerateArray())
            {
                if (!sw.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != "switch0") continue;
                var cpus = new List<int>();
                if (sw.TryGetProperty("cpu-ports", out var cp) && cp.ValueKind == JsonValueKind.Array)
                    foreach (var c in cp.EnumerateArray())
                        if (c.TryGetProperty("cpu-port", out var n) && n.ValueKind == JsonValueKind.Number) cpus.Add(n.GetInt32());
                if (!cpus.Contains(swconfigCpu)) return null;
                var map = new Dictionary<int, string>();
                if (sw.TryGetProperty("edge-ports", out var edges) && edges.ValueKind == JsonValueKind.Array)
                    foreach (var e in edges.EnumerateArray())
                        if (e.TryGetProperty("edge-port", out var p) && p.ValueKind == JsonValueKind.Number
                            && e.TryGetProperty("interface", out var iface) && iface.ValueKind == JsonValueKind.Object
                            && iface.TryGetProperty("id", out var name) && name.ValueKind == JsonValueKind.String
                            && name.GetString() is { Length: > 0 } ifName && swconfigPorts.Contains(p.GetInt32()))
                            map[p.GetInt32()] = ifName;
                return map.Count > 0 ? map : null;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }
}
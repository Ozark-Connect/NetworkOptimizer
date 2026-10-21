namespace NetworkOptimizer.AgentProtocol;

/// <summary>A kernel log line written by the gateway agent's outbound-drop LOG rule.</summary>
public sealed record GatewayDropLog(string ClientIp, string DestIp, string Protocol, int DestPort)
{
    /// <summary>
    /// Reads a netfilter LOG line ("NETOPT_OUT:IN=br0 OUT=eth4 ... SRC=a DST=b ... PROTO=TCP SPT=x DPT=443"),
    /// or null when the line is not one or lacks an address.
    /// </summary>
    public static GatewayDropLog? Parse(string line, string prefix)
    {
        var at = line.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return null;
        string? src = null, dst = null, proto = null;
        var dport = 0;
        foreach (var field in line[(at + prefix.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = field.IndexOf('=');
            if (eq <= 0) continue;
            var value = field[(eq + 1)..];
            switch (field[..eq])
            {
                case "SRC": src ??= value; break;
                case "DST": dst ??= value; break;
                case "PROTO": proto ??= value; break;
                case "DPT": if (dport == 0) int.TryParse(value, out dport); break;
            }
        }
        return src == null || dst == null ? null : new GatewayDropLog(src, dst, proto ?? "", dport);
    }

    /// <summary>
    /// Address-to-MAC pairs from <c>ip neigh show</c> output ("2001:db8::20 dev br0 lladdr aa:bb:cc:dd:ee:ff REACHABLE"),
    /// MACs lowercase. Entries without a link-layer address (FAILED, INCOMPLETE) are skipped.
    /// </summary>
    public static Dictionary<string, string> ParseNeighbors(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var at = Array.IndexOf(parts, "lladdr");
            if (parts.Length > 0 && at > 0 && at + 1 < parts.Length)
                map.TryAdd(parts[0], parts[at + 1].ToLowerInvariant());
        }
        return map;
    }
}

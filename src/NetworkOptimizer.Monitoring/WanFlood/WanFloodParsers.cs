using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Monitoring.WanFlood;

/// <summary>One CPU's cumulative jiffies from <c>/proc/stat</c>.</summary>
public readonly record struct CpuTimes(long Total, long Softirq, long Idle);

/// <summary>One CPU's row of <c>/proc/net/softnet_stat</c> (cumulative).</summary>
public readonly record struct SoftnetRow(long Processed, long Dropped, long TimeSqueeze);

/// <summary>One rule's counters from <c>iptables -nvxL</c>.</summary>
public readonly record struct IptablesRule(long Packets, long Bytes, string Target);

/// <summary>
/// One entry of a hashlimit table (<c>/proc/net/ipt_hashlimit/&lt;name&gt;</c>). The half of the key a
/// table's mode does not use reads as zero.
/// </summary>
public readonly record struct HashlimitEntry(string Source, int SourcePort, string Destination, int DestinationPort,
    ulong Credit, ulong CreditCap, ulong Cost)
{
    /// <summary>The key is over its limit right now: it has less credit than one packet costs.</summary>
    public bool OverLimit => Credit < Cost;
}

/// <summary>Parsers for the kernel files and command output WAN Flood Protection reads on a gateway.</summary>
public static partial class WanFloodParsers
{
    /// <summary>The kernel log line of a full hashlimit table, up to the number.</summary>
    public const string TableFullLogMarker = "xt_hashlimit: max count of ";

    /// <summary>Per-CPU times from <c>/proc/stat</c>, keyed by CPU number. The aggregate <c>cpu</c> line is skipped.</summary>
    public static Dictionary<int, CpuTimes> ParseProcStat(string text)
    {
        var result = new Dictionary<int, CpuTimes>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal) || line.Length < 4 || !char.IsDigit(line[3])) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || !int.TryParse(parts[0].AsSpan(3), out var cpu)) continue;
            // user nice system idle iowait irq softirq [steal guest guest_nice]
            long total = 0;
            for (var i = 1; i < parts.Length && i <= 8; i++)
                total += long.TryParse(parts[i], out var v) ? v : 0;
            var idle = long.Parse(parts[4], CultureInfo.InvariantCulture) + long.Parse(parts[5], CultureInfo.InvariantCulture);
            result[cpu] = new CpuTimes(total, long.Parse(parts[7], CultureInfo.InvariantCulture), idle);
        }
        return result;
    }

    /// <summary>Softirq share of a CPU between two samples, 0-100. Zero when no time passed.</summary>
    public static double SoftirqPercent(CpuTimes before, CpuTimes after)
    {
        var total = after.Total - before.Total;
        return total <= 0 ? 0 : Math.Clamp(100.0 * (after.Softirq - before.Softirq) / total, 0, 100);
    }

    /// <summary>
    /// Rows of <c>/proc/net/softnet_stat</c> by line, which is the online CPU order on 5.4 (the CPU column
    /// arrived later). Columns are hex: processed, dropped, time_squeeze.
    /// </summary>
    public static List<SoftnetRow> ParseSoftnetStat(string text)
    {
        var rows = new List<SoftnetRow>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            rows.Add(new SoftnetRow(Hex(parts[0]), Hex(parts[1]), Hex(parts[2])));
        }
        return rows;
    }

    /// <summary>
    /// The sums of <c>drop</c> and <c>early_drop</c> across CPUs from <c>/proc/net/stat/nf_conntrack</c>, found by
    /// the header row's names: the table turning connections away. Null when the file has neither.
    /// </summary>
    /// <remarks>
    /// <c>insert_failed</c> is left out on purpose: parallel A/AAAA DNS lookups raise it all day on multi-core
    /// gateways, so counting it reports a full table that is not.
    /// </remarks>
    public static long? ParseConntrackDrops(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;
        var header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var columns = new[] { "drop", "early_drop" }
            .Select(n => Array.IndexOf(header, n)).Where(i => i >= 0).ToList();
        if (columns.Count == 0) return null;
        long sum = 0;
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var c in columns)
                if (c < parts.Length) sum += Hex(parts[c]);
        }
        return sum;
    }

    /// <summary>Rules of one chain from <c>iptables -nvxL &lt;chain&gt;</c>, in order.</summary>
    public static List<IptablesRule> ParseIptablesCounters(string text)
    {
        var rules = new List<IptablesRule>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !long.TryParse(parts[0], out var packets) || !long.TryParse(parts[1], out var bytes)) continue;
            // A rule with no target prints its protocol in the target column; it has no verdict either way.
            rules.Add(new IptablesRule(packets, bytes, parts[2]));
        }
        return rules;
    }

    /// <summary>
    /// Rules of one chain from <c>iptables-save -c</c>, in order: <c>[packets:bytes] -A CHAIN ... -j TARGET</c>.
    /// A rule with no target reads as an empty target. iptables-save takes no xtables lock, unlike <c>-L</c>.
    /// </summary>
    public static List<IptablesRule> ParseSaveCounters(string save, string chain)
    {
        var rules = new List<IptablesRule>();
        var prefix = $"-A {chain} ";
        foreach (var raw in save.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith('[')) continue;
            var close = line.IndexOf(']');
            if (close < 0 || !line[(close + 1)..].TrimStart().StartsWith(prefix, StringComparison.Ordinal)) continue;
            var counts = line[1..close].Split(':');
            if (counts.Length != 2 || !long.TryParse(counts[0], out var packets) || !long.TryParse(counts[1], out var bytes)) continue;
            var target = SaveTarget().Match(line) is { Success: true } m ? m.Groups[1].Value : "";
            rules.Add(new IptablesRule(packets, bytes, target));
        }
        return rules;
    }

    /// <summary>Packets of the last DROP rule of a chain, or null when it has none (UniFi's final WAN-local drop).</summary>
    public static long? LastDropPackets(IReadOnlyList<IptablesRule> rules) =>
        rules.LastOrDefault(r => r.Target == "DROP") is { Target: "DROP" } last ? last.Packets : null;

    /// <summary>Packets of every DROP rule of a chain, summed.</summary>
    public static long DropPackets(IReadOnlyList<IptablesRule> rules) => rules.Where(r => r.Target == "DROP").Sum(r => r.Packets);

    /// <summary>Entries of a hashlimit table: <c>expires src:sport->dst:dport credit credit_cap cost</c>.</summary>
    public static List<HashlimitEntry> ParseHashlimit(string text)
    {
        var entries = new List<HashlimitEntry>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            var arrow = parts[1].IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            if (!SplitEndpoint(parts[1][..arrow], out var src, out var sport)
                || !SplitEndpoint(parts[1][(arrow + 2)..], out var dst, out var dport)) continue;
            if (!ulong.TryParse(parts[2], out var credit) || !ulong.TryParse(parts[3], out var cap) || !ulong.TryParse(parts[4], out var cost)) continue;
            entries.Add(new HashlimitEntry(src, sport, dst, dport, credit, cap, cost));
        }
        return entries;
    }

    // "1.2.3.4:53" or an IPv6 address with a trailing ":port"; the port is after the last colon.
    private static bool SplitEndpoint(string text, out string address, out int port)
    {
        var colon = text.LastIndexOf(':');
        address = colon > 0 ? text[..colon] : "";
        port = 0;
        return colon > 0 && int.TryParse(text[(colon + 1)..], out port);
    }

    /// <summary>Packets a tc filter's action has seen, summed over its actions (<c>tc -s filter show</c>).</summary>
    public static long ParseTcFilterPackets(string text) =>
        TcSent().Matches(text).Sum(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));

    /// <summary>
    /// Packets dropped by a device's leaf qdiscs (<c>tc -s qdisc show dev ifbX</c>), summed: the Smart Queues ingress
    /// shaper's loss. Classful parents (htb, hfsc, prio) are skipped so a drop is not counted twice.
    /// </summary>
    public static long ParseLeafQdiscDrops(string text)
    {
        long total = 0;
        string? kind = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("qdisc ", StringComparison.Ordinal))
            {
                kind = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                continue;
            }
            if (kind is null or "htb" or "hfsc" or "prio" or "ingress" or "clsact") continue;
            if (QdiscDropped().Match(line) is { Success: true } m)
                total += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return total;
    }

    /// <summary>The priorities of the filters on a qdisc (<c>tc filter show dev X parent ffff:</c>).</summary>
    public static HashSet<int> ParseTcPrefs(string text) =>
        TcPref().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    /// <summary>The table size of a <c>xt_hashlimit: max count of N reached</c> kernel log line, or null.</summary>
    public static int? ParseHashlimitFull(string line)
    {
        var at = line.IndexOf(TableFullLogMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = line[(at + TableFullLogMarker.Length)..];
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>
    /// Local ports with a socket on them, from <c>/proc/net/udp</c> or <c>/proc/net/tcp</c> (and the
    /// <c>6</c> twins). For TCP only listening sockets count (state 0A); for UDP every bound socket.
    /// </summary>
    public static HashSet<int> ParseProcNetPorts(string text, bool tcp)
    {
        var ports = new HashSet<int>();
        foreach (var line in text.Split('\n').Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (tcp && parts[3] != "0A") continue;
            var colon = parts[1].LastIndexOf(':');
            if (colon < 0) continue;
            if (int.TryParse(parts[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port))
                ports.Add(port);
        }
        return ports;
    }

    /// <summary>Destination ports DNAT rules match, from <c>iptables -t nat -S</c>.</summary>
    public static HashSet<int> ParseDnatPorts(string text)
    {
        var ports = new HashSet<int>();
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("-j DNAT", StringComparison.Ordinal)) continue;
            // A single port, a range (a:b), or a multiport list whose items may themselves be ranges.
            foreach (Match m in DnatPort().Matches(line))
            {
                foreach (var item in m.Groups[1].Value.Split(','))
                {
                    var colon = item.IndexOf(':');
                    if (colon > 0 && int.TryParse(item[..colon], out var lo) && int.TryParse(item[(colon + 1)..], out var hi)
                        && lo <= hi && hi <= 65535)
                        for (var p = lo; p <= hi; p++) ports.Add(p);
                    else if (int.TryParse(item, out var port)) ports.Add(port);
                }
            }
        }
        return ports;
    }

    /// <summary>The first IPv4 address of <c>ip -4 -o addr show dev X</c>, or null.</summary>
    public static string? ParseInterfaceIpv4(string text)
    {
        var m = InetAddress().Match(text);
        return m.Success && IPAddress.TryParse(m.Groups[1].Value, out _) ? m.Groups[1].Value : null;
    }

    /// <summary>The CPUs set in an RPS mask (<c>rps_cpus</c>, hex with optional commas).</summary>
    public static HashSet<int> ParseCpuMask(string text)
    {
        var cpus = new HashSet<int>();
        var hex = text.Trim().Replace(",", "");
        var bit = 0;
        for (var i = hex.Length - 1; i >= 0; i--)
        {
            var nibble = Convert.ToInt32(hex[i].ToString(), 16);
            for (var b = 0; b < 4; b++, bit++)
                if ((nibble & (1 << b)) != 0) cpus.Add(bit);
        }
        return cpus;
    }

    /// <summary>
    /// Scans <c>/proc/net/nf_conntrack</c> for confirmed entries (the file lists only those). Reports
    /// whether one runs from <paramref name="source"/> to <paramref name="destination"/>:<paramref name="port"/>,
    /// whether one has <paramref name="port"/> as the reply's destination port (a NAT'd outbound flow on it),
    /// and whether any runs to <paramref name="destination"/>:<paramref name="port"/> at all (a port in use).
    /// </summary>
    public static (bool FromSource, bool ReplyToPort, bool AnyToPort) ScanConntrack(TextReader reader, string? source, string destination,
        string protocol, int port)
    {
        bool fromSource = false, replyToPort = false, anyToPort = false;
        var dportText = $"dport={port}";
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.Contains($" {protocol} ", StringComparison.Ordinal) || !line.Contains(dportText, StringComparison.Ordinal)) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Original tuple: the first src/dst/dport; reply tuple: the second set.
            var srcs = parts.Where(p => p.StartsWith("src=", StringComparison.Ordinal)).Select(p => p[4..]).ToList();
            var dsts = parts.Where(p => p.StartsWith("dst=", StringComparison.Ordinal)).Select(p => p[4..]).ToList();
            var dports = parts.Where(p => p.StartsWith("dport=", StringComparison.Ordinal)).Select(p => p[6..]).ToList();
            if (srcs.Count < 2 || dsts.Count < 2 || dports.Count < 2) continue;
            var portText = port.ToString(CultureInfo.InvariantCulture);
            if (dsts[0] == destination && dports[0] == portText)
            {
                anyToPort = true;
                if (source != null && srcs[0] == source) fromSource = true;
            }
            if (dsts[1] == destination && dports[1] == portText) replyToPort = true;
            if (fromSource && replyToPort && anyToPort) break;
        }
        return (fromSource, replyToPort, anyToPort);
    }

    /// <summary>
    /// One pass over <c>/proc/net/nf_conntrack</c> for several (source, destination, protocol, port) keys: the
    /// keys that have a confirmed entry. A full table can hold 100k+ lines, so it is read once, not per key.
    /// </summary>
    public static HashSet<(string Source, string Destination, string Protocol, int Port)> ScanConntrackSessions(
        TextReader reader, IReadOnlyCollection<(string Source, string Destination, string Protocol, int Port)> keys)
    {
        var found = new HashSet<(string, string, string, int)>();
        if (keys.Count == 0) return found;
        var wanted = keys.ToHashSet();
        string? line;
        while ((line = reader.ReadLine()) != null && found.Count < wanted.Count)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            string? src = null, dst = null, dport = null;
            foreach (var p in parts)
            {
                if (src == null && p.StartsWith("src=", StringComparison.Ordinal)) src = p[4..];
                else if (dst == null && p.StartsWith("dst=", StringComparison.Ordinal)) dst = p[4..];
                else if (dport == null && p.StartsWith("dport=", StringComparison.Ordinal)) { dport = p[6..]; break; }
            }
            if (src == null || dst == null || !int.TryParse(dport, out var port)) continue;
            var key = (src, dst, parts[2], port);
            if (wanted.Contains(key)) found.Add(key);
        }
        return found;
    }

    private static long Hex(string s) =>
        long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [GeneratedRegex(@"-j (\S+)")]
    private static partial Regex SaveTarget();

    [GeneratedRegex(@"Sent \d+ bytes \d+ pkt \(dropped (\d+)")]
    private static partial Regex QdiscDropped();

    [GeneratedRegex(@"Sent \d+ bytes (\d+) pkt")]
    private static partial Regex TcSent();

    [GeneratedRegex(@"\bpref (\d+)\b")]
    private static partial Regex TcPref();

    [GeneratedRegex(@"--dports? (\S+)")]
    private static partial Regex DnatPort();

    [GeneratedRegex(@"\binet (\d+\.\d+\.\d+\.\d+)/")]
    private static partial Regex InetAddress();
}

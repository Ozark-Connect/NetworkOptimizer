using System.Security.Cryptography;
using System.Text;

namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// The rules of WAN Flood Protection, shared by the gateway agent and the SSH-deployed script so both
/// install the same thing. Two mangle chains hold the hashlimit rules: <see cref="Chain"/>, jumped to from
/// PREROUTING for new connections addressed to the gateway (port forwards included, since DNAT has not run
/// yet), and <see cref="ForwardChain"/>, jumped to from FORWARD for new port-forwarded connections. A tc u32
/// drop on WAN ingress, one priority per drop, is added only by the detector.
/// </summary>
/// <remarks>
/// A full hashlimit table drops every packet that needs a new entry, whatever the rule's target (the 5.4
/// kernel's <c>dsthash_alloc_init</c> returns NULL and the match hotdrops), so a match-only rule drops too.
/// Two rules keep the tables from filling below the point where the gateway saturates anyway, and must not be
/// relaxed: no table is keyed on source and destination port together (a reflection flood makes nearly
/// every packet a new key), and every entry expires after <see cref="TableExpireMs"/> idle (a flooding
/// source is never idle that long, so the short expire changes no limit that matters).
/// </remarks>
public static class WanFloodRules
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "wan-flood";

    /// <summary>
    /// Sent instead by an on-gateway agent whose gateway failed the probe, so the server can tell a gateway that
    /// cannot run it from an agent too old to know about it (which sends neither).
    /// </summary>
    public const string UnavailableCapability = "wan-flood-unavailable";

    public const string Chain = "NETOPT_FLOOD";
    public const string ForwardChain = "NETOPT_FLOOD_FWD";
    public const string GuardV4 = "netopt_flood_guard_v4";
    public const string GuardV6 = "netopt_flood_guard_v6";

    /// <summary>tc priorities of the reactive drops: one per drop, ahead of UniFi's redirect at 49152.</summary>
    public const int DropPrefFirst = 100;
    /// <summary>tc priorities of the measurement filters (action continue), one per candidate.</summary>
    public const int MeasurePrefFirst = 200;
    /// <summary>Most drops (and measurement filters) per WAN.</summary>
    public const int MaxSlots = 64;
    /// <summary>UniFi's ingress filter (connmark + mirred to the Smart Queues IFB). Never touched.</summary>
    public const int UniFiRedirectPref = 49152;

    public const int SourceTableMax = 262144;
    public const int PortTableMax = 65536;
    public const int ForwardTableMax = 4096;
    /// <summary>Idle lifetime of an entry in every table, in milliseconds. Longer lets a spread flood fill a table.</summary>
    public const int TableExpireMs = 2000;

    /// <summary>
    /// The amplification ports from the shared port registry. A NEW UDP packet from one of them is a reflection
    /// flood: a real answer from these services is a reply to the site and never NEW. <c>multiport</c> takes 15 at
    /// most, so the registry may flag no more than that.
    /// </summary>
    public static IReadOnlyList<int> ReflectionSourcePorts => NetworkOptimizer.Core.Helpers.NetworkUtilities.AmplificationPorts;

    /// <summary>The source of the test drop: TEST-NET-2, which no real traffic comes from.</summary>
    public const string TestDropSource = "198.51.100.1";
    /// <summary>The port of the test drop (discard).</summary>
    public const int TestDropPort = 9;

    /// <summary>The per-second limits the chains are built with.</summary>
    public sealed record Limits(int UdpPerSource, int UdpBurst, int SynPerSource, int SynBurst,
        int ForwardedPort, int ForwardedBurst, int ForwardedCapped, int Attribution);

    /// <summary>A per-port limit on new connections to the gateway, armed by the detector.</summary>
    public sealed record PortLimit(string Protocol, int Port, int PerSecond);

    /// <summary>A tighter limit on one port forward, armed while the conntrack table is under pressure.</summary>
    public sealed record ForwardCap(string Protocol, string InternalIp, int Port);

    /// <summary>What a chain holds besides the always-on rules.</summary>
    public sealed record ChainState(bool Enforce, bool PerSource, bool Attribution, bool ForwardAttribution,
        IReadOnlyList<PortLimit> Ports, IReadOnlyList<ForwardCap> ForwardCaps)
    {
        /// <summary>The always-on per-port-forward limit. Off while its table is full (a wide forwarded range being scanned).</summary>
        public bool ForwardLimits { get; init; } = true;

        public static ChainState Steady(bool enforce) => new(enforce, true, false, false, [], []);
    }

    private static string Opts(int size, int max) =>
        $"--hashlimit-htable-size {size} --hashlimit-htable-max {max} --hashlimit-htable-expire {TableExpireMs}";

    private static string SourceOpts(bool v6) => Opts(16384, SourceTableMax) + (v6 ? " --hashlimit-srcmask 64" : "");
    private static string PortOpts => Opts(4096, PortTableMax);
    private static string ForwardOpts => Opts(256, ForwardTableMax);

    // A rule that names an existing table uses that table's rate, not its own, so a table is named for its
    // parameters: a changed limit gets a new table instead of silently keeping the old one.
    private static string Limit(int perSecond, int burst, string mode, string prefix, string opts) =>
        $"-m hashlimit --hashlimit-above {perSecond}/sec --hashlimit-burst {burst} --hashlimit-mode {mode} --hashlimit-name {TableName(prefix, perSecond, burst, mode, opts)} {opts}";

    /// <summary>A hashlimit table's name: a short prefix and a hash of its parameters (15 characters at most).</summary>
    public static string TableName(string prefix, int perSecond, int burst, string mode, string opts) =>
        prefix + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{perSecond}|{burst}|{mode}|{opts}")))[..6].ToLowerInvariant();

    /// <summary>A protocol's source attribution table (keyed on source, IPv4), read by the detector.</summary>
    public static string AttributionSourceTable(Limits l, string protocol) =>
        TableName(protocol == "tcp" ? "nast" : "nasu", l.Attribution, l.Attribution, "srcip", SourceOpts(false));

    /// <summary>A protocol's port attribution table (keyed on destination port, IPv4), read by the detector.</summary>
    public static string AttributionPortTable(Limits l, string protocol) =>
        TableName(protocol == "tcp" ? "napt" : "napu", l.Attribution, l.Attribution, "dstport", PortOpts);

    /// <summary>The port-forward attribution table (keyed on internal address and port), read by the detector.</summary>
    public static string ForwardAttributionTable(Limits l) => TableName("nfa", l.Attribution, l.Attribution, "dstip,dstport", ForwardOpts);

    /// <summary>The directory the kernel lists a family's hashlimit tables in.</summary>
    public static string HashlimitProcDir(bool v6) => v6 ? "/proc/net/ip6t_hashlimit" : "/proc/net/ipt_hashlimit";

    /// <summary>
    /// The rules of <see cref="Chain"/> in order. Detect mode drops the DROP targets, so the counters and
    /// tables still fill.
    /// </summary>
    public static List<string> Rules(bool v6, Limits l, ChainState state)
    {
        var drop = state.Enforce ? " -j DROP" : "";
        var guard = v6 ? GuardV6 : GuardV4;
        var rules = new List<string>
        {
            $"-A {Chain} -m set --match-set {guard} src -j RETURN",
            // Reflection: only NEW packets reach this chain, and a real DNS/NTP reply is ESTABLISHED. 53 and 123 to
            // themselves are server-to-server (DNS NOTIFY, NTP peering) and are left alone.
            $"-A {Chain} -p udp -m multiport --sports {string.Join(',', ReflectionSourcePorts)} -m multiport ! --dports 53,123{drop}",
        };
        // Attribution is IPv4 only: it names targets for the tc drop, which is IPv4 only. Match-only rules.
        if (state.Attribution && !v6)
        {
            foreach (var (protocol, match) in new[] { ("udp", "-p udp"), ("tcp", "-p tcp --syn") })
            {
                var t = protocol == "tcp" ? "t" : "u";
                if (state.PerSource)
                    rules.Add($"-A {Chain} {match} {Limit(l.Attribution, l.Attribution, "srcip", "nas" + t, SourceOpts(false))}");
                rules.Add($"-A {Chain} {match} {Limit(l.Attribution, l.Attribution, "dstport", "nap" + t, PortOpts)}");
            }
        }
        if (state.PerSource)
        {
            rules.Add($"-A {Chain} -p udp {Limit(l.UdpPerSource, l.UdpBurst, "srcip", "nus", SourceOpts(v6))}{drop}");
            rules.Add($"-A {Chain} -p tcp --syn {Limit(l.SynPerSource, l.SynBurst, "srcip", "nss", SourceOpts(v6))}{drop}");
        }
        foreach (var p in state.Ports)
        {
            var match = p.Protocol == "tcp" ? $"-p tcp --syn --dport {p.Port}" : $"-p udp --dport {p.Port}";
            var name = p.Protocol == "tcp" ? "nsp" : "nup";
            rules.Add($"-A {Chain} {match} {Limit(p.PerSecond, p.PerSecond, "dstport", name, PortOpts)}{drop}");
        }
        return rules;
    }

    /// <summary>The rules of <see cref="ForwardChain"/> in order.</summary>
    public static List<string> ForwardRules(bool v6, Limits l, ChainState state)
    {
        var drop = state.Enforce ? " -j DROP" : "";
        var rules = new List<string> { $"-A {ForwardChain} -m set --match-set {(v6 ? GuardV6 : GuardV4)} src -j RETURN" };
        if (state.ForwardLimits)
            rules.Add($"-A {ForwardChain} {Limit(l.ForwardedPort, l.ForwardedBurst, "dstip,dstport", "nfp", ForwardOpts)}{drop}");
        if (state.ForwardAttribution)
            rules.Add($"-A {ForwardChain} {Limit(l.Attribution, l.Attribution, "dstip,dstport", "nfa", ForwardOpts)}");
        foreach (var c in state.ForwardCaps.Where(c => c.InternalIp.Contains(':') == v6))
        {
            var match = c.Protocol == "tcp" ? $"-p tcp --syn --dport {c.Port}" : $"-p udp --dport {c.Port}";
            rules.Add($"-A {ForwardChain} -d {c.InternalIp} {match} {Limit(l.ForwardedCapped, l.ForwardedCapped, "dstip,dstport", "nfc", ForwardOpts)}{drop}");
        }
        return rules;
    }

    /// <summary>A short identity of a rule list. Kept by the agent, never written to the gateway.</summary>
    public static string Fingerprint(IEnumerable<string> rules) =>
        "nof-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rules))))[..12].ToLowerInvariant();

    /// <summary>
    /// An <c>iptables-restore --noflush</c> input that replaces one chain atomically. The replacement keeps
    /// hashlimit tables whose name carries over (the new rule takes a reference before the old one drops
    /// it), so a rewrite does not reset a flooding source's credit.
    /// </summary>
    /// <remarks>
    /// No rule carries a comment: UniFi's provisioner reports any commented rule it does not own as
    /// unexpected. The agent compares the live chain to its own snapshot instead.
    /// </remarks>
    public static string Restore(string chain, IReadOnlyList<string> rules)
    {
        var sb = new StringBuilder();
        sb.Append("*mangle\n");
        sb.Append(':').Append(chain).Append(" - [0:0]\n");
        foreach (var rule in rules) sb.Append(rule).Append('\n');
        sb.Append("COMMIT\n");
        return sb.ToString();
    }

    /// <summary>The PREROUTING jump for one WAN (arguments after <c>-t mangle -I PREROUTING 1</c>).</summary>
    public static string[] Jump(string wan) =>
        ["-i", wan, "-m", "addrtype", "--dst-type", "LOCAL", "-m", "conntrack", "--ctstate", "NEW", "-j", Chain];

    /// <summary>
    /// The FORWARD jump for one WAN. Two conntrack matches are ANDed; one <c>--ctstate NEW,DNAT</c> would be
    /// OR. Mangle FORWARD runs before the entry confirms, so a drop here never occupies the conntrack table.
    /// </summary>
    public static string[] ForwardJump(string wan) =>
        ["-i", wan, "-m", "conntrack", "--ctstate", "NEW", "-m", "conntrack", "--ctstate", "DNAT", "-j", ForwardChain];

    /// <summary>The tc priority of a drop slot (1-based).</summary>
    public static int DropPref(int slot) => DropPrefFirst + slot - 1;

    /// <summary>The tc priority of a measurement slot (1-based).</summary>
    public static int MeasurePref(int slot) => MeasurePrefFirst + slot - 1;

    /// <summary>Whether a tc priority is one of ours. Everything else on the qdisc belongs to UniFi.</summary>
    public static bool IsOurPref(int pref) =>
        (pref >= DropPrefFirst && pref < DropPrefFirst + MaxSlots) || (pref >= MeasurePrefFirst && pref < MeasurePrefFirst + MaxSlots);

    /// <summary>
    /// Arguments for <c>tc</c> that add a u32 filter on the WAN's ingress: <paramref name="source"/> null
    /// matches the port alone. <paramref name="drop"/> false adds a measurement filter, whose
    /// <c>continue</c> hands the packet on to UniFi's redirect. Never <c>pass</c>: that ends classification
    /// and skips the redirect, which bypasses Smart Queues.
    /// </summary>
    public static string[] TcFilterAdd(string wan, int pref, string? source, string destination, string protocol, int port, bool drop)
    {
        if (!IsOurPref(pref)) throw new ArgumentOutOfRangeException(nameof(pref), pref, "not a WAN Flood Protection priority");
        var args = new List<string> { "filter", "add", "dev", wan, "parent", "ffff:", "protocol", "ip", "pref", pref.ToString(), "u32" };
        if (source != null) args.AddRange(["match", "ip", "src", $"{source}/32"]);
        args.AddRange(["match", "ip", "dst", $"{destination}/32"]);
        args.AddRange(["match", "ip", "protocol", protocol == "tcp" ? "6" : "17", "0xff"]);
        args.AddRange(["match", "ip", "dport", port.ToString(), "0xffff"]);
        args.AddRange(["action", drop ? "drop" : "continue"]);
        return [.. args];
    }

    /// <summary>Arguments for <c>tc</c> that remove every filter at one of our priorities.</summary>
    public static string[] TcFilterDel(string wan, int pref)
    {
        if (!IsOurPref(pref)) throw new ArgumentOutOfRangeException(nameof(pref), pref, "not a WAN Flood Protection priority");
        return ["filter", "del", "dev", wan, "parent", "ffff:", "pref", pref.ToString()];
    }

    /// <summary>
    /// A POSIX shell script that removes everything WAN Flood Protection adds: the jumps (found by target,
    /// so a WAN dropped from the config is cleaned too), both chains, the guard sets, our tc priorities on
    /// <paramref name="wans"/>, and the ingress qdiscs in <paramref name="ownedQdiscs"/>. Safe when any of it
    /// is already gone.
    /// </summary>
    public static string Teardown(IEnumerable<string> wans, IEnumerable<string> ownedQdiscs)
    {
        var sb = new StringBuilder();
        sb.Append("for t in iptables ip6tables; do ");
        foreach (var (builtin, chain) in new[] { ("PREROUTING", Chain), ("FORWARD", ForwardChain) })
            sb.Append($"$t -w -t mangle -S {builtin} 2>/dev/null | grep -e '-j {chain}$' | sed 's/^-A/-D/' | while read -r r; do $t -w -t mangle $r; done; ");
        sb.Append($"for c in {Chain} {ForwardChain}; do $t -w -t mangle -F $c 2>/dev/null; $t -w -t mangle -X $c 2>/dev/null; done; done; ");
        sb.Append($"ipset destroy {GuardV4} 2>/dev/null; ipset destroy {GuardV4}_tmp 2>/dev/null; ");
        sb.Append($"ipset destroy {GuardV6} 2>/dev/null; ipset destroy {GuardV6}_tmp 2>/dev/null; ");
        foreach (var wan in wans.Distinct())
        {
            sb.Append($"for base in {DropPrefFirst} {MeasurePrefFirst}; do p=$base; while [ $p -lt $((base + {MaxSlots})) ]; do ");
            sb.Append($"tc filter del dev {wan} parent ffff: pref $p 2>/dev/null; p=$((p + 1)); done; done; ");
        }
        foreach (var wan in ownedQdiscs.Distinct())
            sb.Append($"tc qdisc del dev {wan} ingress 2>/dev/null; ");
        sb.Append("true");
        return sb.ToString();
    }

    /// <summary>Whether a WAN interface name is safe to put in a command or script.</summary>
    public static bool IsValidInterface(string name) =>
        name.Length is > 0 and <= 15 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@');
}

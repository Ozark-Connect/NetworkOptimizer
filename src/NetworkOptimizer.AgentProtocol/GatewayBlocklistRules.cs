namespace NetworkOptimizer.AgentProtocol;

/// <summary>The iptables rules of the gateway agent's blocklist chain, in order (iptables -S form).</summary>
public static class GatewayBlocklistRules
{
    /// <summary>The agent's chain, jumped to from FORWARD and INPUT.</summary>
    public const string ChainName = "NETOPT_BLOCK";
    public const string BlockV4 = "netopt_block_v4";
    public const string BlockV6 = "netopt_block_v6";
    public const string FeedV4 = "netopt_feed_v4";
    public const string FeedV6 = "netopt_feed_v6";
    public const string GuardV4 = "netopt_guard_v4";
    public const string GuardV6 = "netopt_guard_v6";
    public const string OutV4 = "netopt_out_v4";
    public const string OutV6 = "netopt_out_v6";
    /// <summary>Kernel log prefix of an outbound drop.</summary>
    public const string OutLogPrefix = "NETOPT_OUT:";
    public const int OutLogPerMinute = 30;

    /// <summary>
    /// The chain as the agent installs it for one family, without the site's exceptions: what the Security
    /// Audit compares the gateway against. Exception RETURNs sit ahead of these rules and are not compared.
    /// </summary>
    public static string[] CoreChain(bool feedsSplit, bool v6) =>
        Chain(ChainName, v6 ? GuardV6 : GuardV4, v6 ? BlockV6 : BlockV4, v6 ? OutV6 : OutV4, OutLogPrefix, OutLogPerMinute,
            feedsSplit ? (v6 ? FeedV6 : FeedV4) : null, exceptions: null, v6);

    /// <summary>
    /// Traffic to an outbound-blocked destination is logged (rate limited) and dropped in every state;
    /// then the server's own addresses are exempt; then new connections from a blocked source are
    /// dropped. Matching new connections only for sources leaves replies to connections a LAN device
    /// opened alone, as UniFi Network's policies do. Outbound matches every state so a session opened
    /// before the list loaded is cut too; that is safe because only the narrow command-server and
    /// criminal-network lists (<c>PublicBlockList.Outbound</c>) are blocked outbound.
    /// </summary>
    /// <remarks>
    /// Outbound comes first: ahead of it, the server exemption let a server on the site's LAN reach listed
    /// addresses. The server is never exempt by destination: port forwards to it would let blocked
    /// sources through after NAT.
    /// <para>
    /// With <paramref name="feedSet"/>, the public list entries have their own set, dropped after the
    /// site's own blocks, and each exception is a RETURN that also matches the list set it covers. So an
    /// exception never lets through a source the site blocked itself, nor anything no list blocks.
    /// </para>
    /// </remarks>
    public static string[] Chain(string chain, string guardSet, string blockSet, string outSet, string logPrefix, int logPerMinute,
        string? feedSet = null, IEnumerable<GatewayBlockException>? exceptions = null, bool v6 = false)
    {
        var rules = new List<string>();
        var family = (exceptions ?? []).Where(e => e.Family() is not { } f || f == v6).ToList();
        if (feedSet != null)
        {
            foreach (var e in family.Where(e => !e.Inbound))
                rules.AddRange(Exception(chain, e.Device, e.Address, e, outSet, "dst", "dport", null, v6));
            // Replies to an allowed inbound connection go back out to the listed address.
            foreach (var e in family.Where(e => e.Inbound))
                rules.AddRange(Exception(chain, e.Device, e.Address, e, outSet, "dst", "sport", "RELATED,ESTABLISHED", v6));
        }
        rules.Add($"-A {chain} -m set --match-set {outSet} dst -m limit --limit {logPerMinute}/min --limit-burst {logPerMinute} -j LOG --log-prefix {logPrefix} --log-level 6");
        rules.Add($"-A {chain} -m set --match-set {outSet} dst -j DROP");
        rules.Add($"-A {chain} -m set --match-set {guardSet} src -j RETURN");
        rules.Add($"-A {chain} -m set --match-set {blockSet} src -m state --state NEW -j DROP");
        if (feedSet != null)
        {
            foreach (var e in family.Where(e => e.Inbound))
                rules.AddRange(Exception(chain, e.Address, e.Device, e, feedSet, "src", "dport", null, v6));
            rules.Add($"-A {chain} -m set --match-set {feedSet} src -m state --state NEW -j DROP");
        }
        return [.. rules];
    }

    /// <summary>
    /// One RETURN per protocol, written as iptables -S prints it (addresses with their prefix, then -p,
    /// then the matches in order), so a steady chain compares equal and is never rewritten.
    /// </summary>
    private static IEnumerable<string> Exception(string chain, string source, string destination, GatewayBlockException e,
        string set, string setDirection, string portFlag, string? states, bool v6)
    {
        var protocols = e.Protocols.Count == 0 ? [""] : e.Protocols.Select(p => p.ToLowerInvariant()).Distinct().ToList();
        foreach (var protocol in protocols)
        {
            var rule = new System.Text.StringBuilder($"-A {chain}");
            if (source.Length > 0) rule.Append(" -s ").Append(WithPrefix(source, v6));
            if (destination.Length > 0) rule.Append(" -d ").Append(WithPrefix(destination, v6));
            if (protocol.Length > 0) rule.Append(" -p ").Append(protocol);
            rule.Append($" -m set --match-set {set} {setDirection}");
            if (protocol.Length > 0 && e.PortFrom > 0)
            {
                rule.Append($" -m {protocol} --{portFlag} {e.PortFrom}");
                if (e.PortTo > e.PortFrom) rule.Append(':').Append(e.PortTo);
            }
            if (states != null) rule.Append(" -m conntrack --ctstate ").Append(states);
            yield return rule.Append(" -j RETURN").ToString();
        }
    }

    /// <summary>
    /// How many times <paramref name="builtin"/> jumps to <paramref name="chain"/> in an <c>iptables -S</c>
    /// listing (unconditional jumps only, as the agent inserts them).
    /// </summary>
    public static int CountJumps(string listing, string builtin, string chain)
    {
        var jump = $"-A {builtin} -j {chain}";
        return listing.Split('\n', StringSplitOptions.TrimEntries).Count(line => line == jump);
    }

    private static string WithPrefix(string address, bool v6) => address.Contains('/') ? address : address + (v6 ? "/128" : "/32");

    /// <summary>True for IPv6, false for IPv4, null when the exception names no address (both families).</summary>
    public static bool? Family(this GatewayBlockException e) =>
        (e.Device.Length > 0 ? e.Device : e.Address) is { Length: > 0 } a ? a.Contains(':') : null;

    /// <summary>
    /// A POSIX shell script that removes the chain, every jump to it from FORWARD and INPUT (both
    /// families), and the sets with their temporary twins. Safe to run when any of it is already gone.
    /// </summary>
    public static string Teardown(string chain, IEnumerable<string> sets) =>
        "for t in iptables ip6tables; do "
        + $"for c in FORWARD INPUT; do while $t -D $c -j {chain} 2>/dev/null; do :; done; done; "
        + $"$t -F {chain} 2>/dev/null; $t -X {chain} 2>/dev/null; done; "
        + $"for s in {string.Join(' ', sets)}; do ipset destroy ${{s}}_tmp 2>/dev/null; ipset destroy $s 2>/dev/null; done; true";
}

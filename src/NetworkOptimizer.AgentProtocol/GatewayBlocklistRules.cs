namespace NetworkOptimizer.AgentProtocol;

/// <summary>The iptables rules of the gateway agent's blocklist chain, in order (iptables -S form).</summary>
public static class GatewayBlocklistRules
{
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
    /// </remarks>
    public static string[] Chain(string chain, string guardSet, string blockSet, string outSet, string logPrefix, int logPerMinute) =>
    [
        $"-A {chain} -m set --match-set {outSet} dst -m limit --limit {logPerMinute}/min --limit-burst {logPerMinute} -j LOG --log-prefix {logPrefix} --log-level 6",
        $"-A {chain} -m set --match-set {outSet} dst -j DROP",
        $"-A {chain} -m set --match-set {guardSet} src -j RETURN",
        $"-A {chain} -m set --match-set {blockSet} src -m state --state NEW -j DROP",
    ];

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

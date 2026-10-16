namespace NetworkOptimizer.AgentProtocol;

/// <summary>The iptables rules of the gateway agent's blocklist chain, in order (iptables -S form).</summary>
public static class GatewayBlocklistRules
{
    /// <summary>
    /// Traffic to an outbound-blocked destination is logged (rate limited) and dropped in every state;
    /// then the server's own addresses are exempt; then new connections from a blocked source are
    /// dropped. Matching new connections only for sources leaves replies to connections a LAN device
    /// opened alone, as UniFi Network's policies do.
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
}

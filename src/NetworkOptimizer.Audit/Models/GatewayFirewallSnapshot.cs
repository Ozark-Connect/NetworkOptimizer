namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// The gateway's own firewall state, read over SSH: the live udapi firewall filter (UniFi Network's compiled
/// policies, each rule tagged with its policy ID), the kernel's filter tables, and the ipset headers. A section
/// is null when its command printed nothing.
/// </summary>
public sealed record GatewayFirewallSnapshot
{
    /// <summary>Output of <c>ubios-udapi-client -r GET /firewall/filter</c>: a JSON array of chains.</summary>
    public string? UdapiFilterJson { get; init; }

    /// <summary>Output of <c>iptables-save -t filter</c>.</summary>
    public string? Iptables { get; init; }

    /// <summary>Output of <c>ip6tables-save -t filter</c>.</summary>
    public string? Ip6tables { get; init; }

    /// <summary>Output of <c>ipset list -t</c> (headers only, no members).</summary>
    public string? IpsetHeaders { get; init; }
}

/// <summary>A WAN network and the interface its traffic uses on the gateway (the data path: <c>eth4</c>, <c>eth6.7</c>, <c>ppp0</c>).</summary>
public sealed record GatewayWanPath(string NetworkId, string NetworkName, string Interface);

/// <summary>
/// What Threat Intelligence's gateway agent blocking should have in place on the gateway, from what the server
/// last pushed to it.
/// </summary>
public sealed record GatewayBlocklistExpectation
{
    /// <summary>The chain the agent installs.</summary>
    public required string ChainName { get; init; }

    /// <summary>The chain's rules for IPv4, in <c>iptables -S</c> form, without the site's exception RETURNs.</summary>
    public required IReadOnlyList<string> CoreRulesV4 { get; init; }

    /// <summary>The same for IPv6.</summary>
    public required IReadOnlyList<string> CoreRulesV6 { get; init; }

    /// <summary>Each set the agent fills, with how many entries the server sent it (0 when none).</summary>
    public required IReadOnlyDictionary<string, int> SentEntries { get; init; }
}

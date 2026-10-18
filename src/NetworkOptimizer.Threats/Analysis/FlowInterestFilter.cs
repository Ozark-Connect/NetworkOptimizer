using System.Text.Json;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Filters traffic flow entries to only those worth normalizing and storing.
/// Applied BEFORE normalization to reduce volume (~10k flows/day to hundreds).
///
/// Design: We want to capture not just what UniFi blocked, but also suspicious
/// allowed traffic that could indicate threats UniFi missed (C2 callbacks,
/// data exfiltration, lateral movement, etc.).
/// </summary>
public static class FlowInterestFilter
{
    /// <summary>
    /// Ports commonly targeted by attackers (incoming).
    /// </summary>
    private static readonly HashSet<int> SensitivePorts = new()
    {
        22, 23, 25, 445, 1433, 1521, 3306, 3389, 5432, 5900, 5985, 5986, 6379, 8080, 8443, 27017
    };

    /// <summary>
    /// Ports commonly used by malware for C2 or exfiltration (outgoing).
    /// </summary>
    private static readonly HashSet<int> SuspiciousOutboundPorts = new()
    {
        4444, 5555, 6666, 6667, 6668, 6669, // Common C2, IRC
        1080, 1194, 1723, // SOCKS, OpenVPN, PPTP (tunneling)
        8888, 9090, 9999, // Common backdoor ports
        31337 // Elite/backdoor
    };

    /// <summary>A flow's destination address, or null.</summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "traffic-flows destination.ip")]
    public static string? DestinationIp(JsonElement flow) =>
        flow.TryGetProperty("destination", out var destination) ? destination.GetPropertyOrDefault("ip", "") is { Length: > 0 } ip ? ip : null : null;

    /// <summary>
    /// Returns true if the flow is interesting enough to store as a ThreatEvent.
    /// </summary>
    public static bool IsInteresting(JsonElement flow)
    {
        // Any blocked action is always interesting
        var action = flow.GetPropertyOrDefault("action", "");
        if (action.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            return true;

        // Medium or high risk flows (UniFi's DPI flagged them)
        var risk = flow.GetPropertyOrDefault("risk", "");
        if (risk.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
            risk.Equals("high", StringComparison.OrdinalIgnoreCase))
            return true;

        var direction = flow.GetPropertyOrDefault("direction", "");

        // Incoming to sensitive ports (even if allowed - potential probe that got through)
        if (direction.Equals("incoming", StringComparison.OrdinalIgnoreCase))
        {
            var destPort = 0;
            if (flow.TryGetProperty("destination", out var dest))
                destPort = dest.GetPropertyOrDefault("port", 0);

            if (SensitivePorts.Contains(destPort))
                return true;
        }

        // Outgoing to suspicious ports (potential C2, exfiltration, tunneling)
        if (direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase))
        {
            var destPort = 0;
            if (flow.TryGetProperty("destination", out var dest))
                destPort = dest.GetPropertyOrDefault("port", 0);

            if (SuspiciousOutboundPorts.Contains(destPort))
                return true;
        }

        // Low risk flows on normal ports are not interesting
        return false;
    }

    /// <summary>
    /// True when every policy that blocked the flow is UniFi's predefined Block Invalid Traffic: late
    /// packets on connections whose conntrack entry is gone, not something anyone sent on purpose.
    /// The flow's policy ids are not the firewall-policies <c>_id</c>s, so the predefined name is the key.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "traffic-flows policies[] internal_type and predefined policy name")]
    public static bool IsInvalidStateDrop(JsonElement flow)
    {
        if (!flow.TryGetProperty("policies", out var policies) || policies.ValueKind != JsonValueKind.Array
            || policies.GetArrayLength() == 0)
            return false;

        foreach (var policy in policies.EnumerateArray())
        {
            if (!policy.GetPropertyOrDefault("internal_type", "").Equals("PREDEFINED_FIREWALL_RULE", StringComparison.Ordinal)
                || !policy.GetPropertyOrDefault("name", "").Equals(InvalidTrafficPolicyName, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>The name UniFi Network gives its predefined invalid-connection-state Block policies.</summary>
    public const string InvalidTrafficPolicyName = "Block Invalid Traffic";

    /// <summary>Server ports that late replies to LAN clients come from.</summary>
    public static readonly IReadOnlySet<int> ServerPorts = new HashSet<int> { 53, 80, 123, 443, 853, 993, 3478, 5222, 5223, 5228, 5349 };

    /// <summary>The ephemeral range a client's side of a connection sits in (Linux starts at 32768, Windows at 49152).</summary>
    public const int EphemeralPortFloor = 32768;

    /// <summary>Whether a flow has the shape of a server's reply to a LAN client: a server port to an ephemeral port.</summary>
    public static bool IsServerToEphemeral(int sourcePort, int destPort) =>
        ServerPorts.Contains(sourcePort) && destPort >= EphemeralPortFloor;

    /// <summary>
    /// A blocked inbound flow shaped like a reply, from a public address. Whether it really is one is decided
    /// against the site's outgoing connections (<see cref="IsReplyTo"/>); the shape alone can be spoofed.
    /// </summary>
    public static bool IsReplyCandidate(Models.ThreatEvent e) =>
        e.EventSource == Models.EventSource.TrafficFlow
        && e.Action == Models.ThreatAction.Blocked
        && string.Equals(e.Direction, "incoming", StringComparison.OrdinalIgnoreCase)
        && IsServerToEphemeral(e.SourcePort, e.DestPort)
        && NetworkOptimizer.Core.Helpers.NetworkUtilities.IsPublicIpAddress(e.SourceIp);

    /// <summary>
    /// Whether a candidate is a late packet on a connection a LAN client opened: a client connected out to
    /// the same address and port, from the port the packet was sent to. NAT keeps the client's port, so a
    /// scanner would have to guess a live connection's port to match.
    /// </summary>
    public static bool IsReplyTo(Models.ThreatEvent e, IReadOnlySet<(string Ip, int RemotePort, int ClientPort)> outgoing) =>
        outgoing.Contains((e.SourceIp.ToLowerInvariant(), e.SourcePort, e.DestPort));

    /// <summary>The remote address and port, and the client's port, of each allowed outgoing flow in a traffic-flows page.</summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "traffic-flows source/destination ip and port")]
    public static IEnumerable<(string Ip, int RemotePort, int ClientPort)> OutgoingConnections(JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var flow in data.EnumerateArray())
        {
            if (!flow.GetPropertyOrDefault("action", "").Equals("allowed", StringComparison.OrdinalIgnoreCase)
                || !flow.TryGetProperty("destination", out var destination) || !flow.TryGetProperty("source", out var source))
                continue;
            var ip = destination.GetPropertyOrDefault("ip", "");
            if (ip.Length == 0
                || !destination.TryGetProperty("port", out var remotePort) || remotePort.ValueKind != JsonValueKind.Number
                || !source.TryGetProperty("port", out var clientPort) || clientPort.ValueKind != JsonValueKind.Number)
                continue;
            yield return (ip.ToLowerInvariant(), remotePort.GetInt32(), clientPort.GetInt32());
        }
    }
}

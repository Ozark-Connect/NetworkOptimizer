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
}

using System.Text.Json;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Filters traffic flow entries to only those worth normalizing and storing.
/// Applied BEFORE normalization to reduce volume (~10k flows/day to hundreds).
/// </summary>
public static class FlowInterestFilter
{
    private static readonly HashSet<int> SensitivePorts = new()
    {
        22, 23, 25, 445, 1433, 1521, 3306, 3389, 5432, 5900, 5985, 5986, 6379, 8080, 8443, 27017
    };

    /// <summary>
    /// Returns true if the flow is interesting enough to store as a ThreatEvent.
    /// </summary>
    public static bool IsInteresting(JsonElement flow)
    {
        // Any blocked action is interesting
        var action = flow.GetPropertyOrDefault("action", "");
        if (action.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            return true;

        // Medium or high risk flows are interesting
        var risk = flow.GetPropertyOrDefault("risk", "");
        if (risk.Equals("medium", StringComparison.OrdinalIgnoreCase) ||
            risk.Equals("high", StringComparison.OrdinalIgnoreCase))
            return true;

        // Incoming to sensitive ports
        var direction = flow.GetPropertyOrDefault("direction", "");
        if (direction.Equals("incoming", StringComparison.OrdinalIgnoreCase))
        {
            var destPort = 0;
            if (flow.TryGetProperty("destination", out var dest))
                destPort = dest.GetPropertyOrDefault("port", 0);

            if (SensitivePorts.Contains(destPort))
                return true;
        }

        return false;
    }
}

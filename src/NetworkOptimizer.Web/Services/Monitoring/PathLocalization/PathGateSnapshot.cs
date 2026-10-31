using System.Text.Json;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>
/// What the gate saw when an episode opened (stored as the episode's GateSnapshot JSON), plus the
/// monitored hops' own verdicts over the episode. Those anchors are not in the stored rounds, and a hop
/// table rebuilt without them would disagree with the verdict they helped reach.
/// </summary>
public sealed class PathGateSnapshot
{
    public sealed record DegradedTarget(string TargetId, double LossPct, double RttRiseMs);

    public List<DegradedTarget> Degraded { get; init; } = new();

    public List<string> CleanControls { get; init; } = new();

    /// <summary>Median WAN utilization over the degradation (fraction of plan), when known.</summary>
    public double? Utilization { get; init; }

    public bool Metered { get; init; }

    /// <summary>Monitored hop address to whether its series was elevated during the episode.</summary>
    public Dictionary<string, bool> Anchors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a stored snapshot; an unreadable or empty one reads as empty rather than failing the episode.</summary>
    public static PathGateSnapshot Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PathGateSnapshot();
        try
        {
            var snapshot = JsonSerializer.Deserialize<PathGateSnapshot>(json) ?? new PathGateSnapshot();
            snapshot.Anchors = new Dictionary<string, bool>(snapshot.Anchors, StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
        catch (JsonException)
        {
            return new PathGateSnapshot();
        }
    }
}

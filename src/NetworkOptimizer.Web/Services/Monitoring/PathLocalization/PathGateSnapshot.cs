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

    /// <summary>The anchors that came from pinging an unmonitored hop during the episode, not from a monitored series.</summary>
    public List<string> PingedHops { get; set; } = new();

    /// <summary>One pinged hop's pooled echoes, for the hop table.</summary>
    public sealed record HopPingStats(int Sent, int Received, double? MinRttMs)
    {
        public double LossPct => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
    }

    /// <summary>Every hop pinged during the episode, by address, with what its echoes returned.</summary>
    public Dictionary<string, HopPingStats> HopPings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitored access-layer hops (Access ISP, WAN L2 neighbor) on the episode's WAN, by address.</summary>
    public List<string> AccessHops { get; set; } = new();

    /// <summary>Each monitored hop's own series loss over the episode, by address: what decided it.</summary>
    public Dictionary<string, double> MonitoredLoss { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitored hops' and destinations' own series baseline RTT, by address, as first read in the episode.</summary>
    public Dictionary<string, double> SeriesBaselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a stored snapshot; an unreadable or empty one reads as empty rather than failing the episode.</summary>
    public static PathGateSnapshot Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PathGateSnapshot();
        try
        {
            var snapshot = JsonSerializer.Deserialize<PathGateSnapshot>(json) ?? new PathGateSnapshot();
            snapshot.Anchors = new Dictionary<string, bool>(snapshot.Anchors, StringComparer.OrdinalIgnoreCase);
            snapshot.HopPings = new Dictionary<string, HopPingStats>(snapshot.HopPings, StringComparer.OrdinalIgnoreCase);
            snapshot.MonitoredLoss = new Dictionary<string, double>(snapshot.MonitoredLoss, StringComparer.OrdinalIgnoreCase);
            snapshot.SeriesBaselines = new Dictionary<string, double>(snapshot.SeriesBaselines, StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
        catch (JsonException)
        {
            return new PathGateSnapshot();
        }
    }
}

namespace NetworkOptimizer.WiFi;

/// <summary>
/// Site-wide Wi-Fi health score (0-100) with dimension breakdowns.
/// Higher is better.
/// </summary>
public class SiteHealthScore
{
    /// <summary>Overall score (0-100)</summary>
    public int OverallScore { get; set; }

    /// <summary>Score grade (A, B, C, D, F)</summary>
    public string Grade => OverallScore switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F"
    };

    /// <summary>Signal quality dimension</summary>
    public ScoreDimension SignalQuality { get; set; } = new();

    /// <summary>Channel health dimension (interference, utilization)</summary>
    public ScoreDimension ChannelHealth { get; set; } = new();

    /// <summary>Roaming performance dimension</summary>
    public ScoreDimension RoamingPerformance { get; set; } = new();

    /// <summary>Airtime efficiency dimension</summary>
    public ScoreDimension AirtimeEfficiency { get; set; } = new();

    /// <summary>Client satisfaction dimension</summary>
    public ScoreDimension ClientSatisfaction { get; set; } = new();

    /// <summary>Capacity headroom dimension</summary>
    public ScoreDimension CapacityHeadroom { get; set; } = new();

    /// <summary>When this score was calculated</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Key issues affecting the score</summary>
    public List<HealthIssue> Issues { get; set; } = new();

    /// <summary>Summary statistics</summary>
    public HealthSummaryStats Stats { get; set; } = new();
}

/// <summary>
/// A single dimension of the health score
/// </summary>
public class ScoreDimension
{
    /// <summary>Dimension name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Score for this dimension (0-100)</summary>
    public int Score { get; set; }

    /// <summary>Weight of this dimension in overall score (0-1)</summary>
    public double Weight { get; set; }

    /// <summary>Brief description of current state</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Detailed breakdown factors</summary>
    public List<ScoreFactor> Factors { get; set; } = new();
}

/// <summary>
/// A factor contributing to a dimension score
/// </summary>
public class ScoreFactor
{
    /// <summary>Factor name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Current value</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Impact on score (positive = good, negative = bad)</summary>
    public int Impact { get; set; }

    /// <summary>Brief explanation</summary>
    public string? Description { get; set; }
}

/// <summary>
/// An issue affecting site health
/// </summary>
public class HealthIssue
{
    /// <summary>Issue severity</summary>
    public HealthIssueSeverity Severity { get; set; }

    /// <summary>Affected dimensions (an issue can affect multiple dimensions)</summary>
    public HashSet<HealthDimension> Dimensions { get; set; } = new();

    /// <summary>Issue title</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Detailed description</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Affected entity (AP name, client name, etc.)</summary>
    public string? AffectedEntity { get; set; }

    /// <summary>MAC address of affected client, for linking to client details</summary>
    public string? AffectedClientMac { get; set; }

    /// <summary>Recommended action</summary>
    public string? Recommendation { get; set; }

    /// <summary>Score impact (negative number)</summary>
    public int ScoreImpact { get; set; }

    /// <summary>
    /// Whether this issue should be shown on the main overview.
    /// Set to false for informational issues that are only relevant on specific tabs.
    /// Defaults to true.
    /// </summary>
    public bool ShowOnOverview { get; set; } = true;
}

/// <summary>
/// Health score dimensions that issues can affect
/// </summary>
public enum HealthDimension
{
    SignalQuality,
    ChannelHealth,
    RoamingPerformance,
    AirtimeEfficiency,
    ClientSatisfaction,
    CapacityHeadroom,
    BandSteering
}

public enum HealthIssueSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>
/// Summary statistics for the site
/// </summary>
public class HealthSummaryStats
{
    public int TotalAps { get; set; }
    public int TotalClients { get; set; }
    public int ClientsOn2_4GHz { get; set; }
    public int ClientsOn5GHz { get; set; }
    public int ClientsOn6GHz { get; set; }
    public double AvgSatisfaction { get; set; }
    public double AvgSignalStrength { get; set; }
    public int WeakSignalClients { get; set; }
    public int LegacyClients { get; set; }
    public double AvgChannelUtilization2_4GHz { get; set; }
    public double AvgChannelUtilization5GHz { get; set; }
    public double AvgChannelUtilization6GHz { get; set; }
    public int TotalRoamsLast24h { get; set; }
    public double RoamSuccessRate { get; set; }
}

namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Result of cross-referencing threat events with port forward rules.
/// </summary>
public class ExposureReport
{
    public List<ExposedService> ExposedServices { get; set; } = [];
    public int TotalExposedPorts { get; set; }
    public int TotalThreatsTargetingExposed { get; set; }
    public GeoBlockRecommendation? GeoBlockRecommendation { get; set; }
}

/// <summary>
/// A port forward rule with associated threat data.
/// </summary>
public class ExposedService
{
    public int Port { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string ServiceName { get; set; } = string.Empty;
    public string ForwardTarget { get; set; } = string.Empty;
    public string? RuleName { get; set; }
    public int ThreatCount { get; set; }
    public int UniqueSourceIps { get; set; }
    public List<string> TopSignatures { get; set; } = [];
    public Dictionary<int, int> SeverityBreakdown { get; set; } = new();
}

/// <summary>
/// Recommendation for geographic IP blocking based on threat source analysis.
/// </summary>
public class GeoBlockRecommendation
{
    public List<string> Countries { get; set; } = [];
    public double PreventionPercentage { get; set; }
    public int TotalDetectedEvents { get; set; }
    public int PreventableEvents { get; set; }
}

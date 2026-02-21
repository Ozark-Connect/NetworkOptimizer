namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Links threat events to port forward rules, showing which exposed services are under attack.
/// This is a DTO, not a database entity.
/// </summary>
public class ExposureMapping
{
    public int Port { get; set; }
    public string Protocol { get; set; } = "tcp";
    public string ServiceName { get; set; } = string.Empty;
    public string ForwardTarget { get; set; } = string.Empty;
    public int ThreatCount { get; set; }
    public int UniqueSourceIps { get; set; }
    public List<string> TopSignatures { get; set; } = [];
    public Dictionary<int, int> SeverityBreakdown { get; set; } = new();
}

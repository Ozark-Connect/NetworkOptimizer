using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// A noise filter rule that excludes matching threat events from the dashboard.
/// Null fields act as wildcards (match any value).
/// Examples:
///   - SourceIp + DestIp + DestPort: exact tuple match
///   - DestIp + DestPort: any source hitting this dest:port
///   - SourceIp only: all events from this source
/// </summary>
public class ThreatNoiseFilter
{
    public int Id { get; set; }

    /// <summary>
    /// Source IP to match (null = any source).
    /// </summary>
    public string? SourceIp { get; set; }

    /// <summary>
    /// Destination IP to match (null = any destination).
    /// </summary>
    public string? DestIp { get; set; }

    /// <summary>
    /// Destination port to match (null = any port).
    /// </summary>
    public int? DestPort { get; set; }

    /// <summary>
    /// Human-readable description of why this filter exists.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Check if this filter matches the given event fields.
    /// Null filter fields are wildcards (match anything).
    /// Null event fields don't match non-null filter fields.
    /// Supports CIDR notation in SourceIp/DestIp (e.g. "10.0.0.0/8").
    /// </summary>
    public bool Matches(string? sourceIp, string? destIp, int? destPort)
    {
        if (SourceIp != null && !IpMatches(SourceIp, sourceIp)) return false;
        if (DestIp != null && !IpMatches(DestIp, destIp)) return false;
        if (DestPort != null && DestPort != destPort) return false;
        return true;
    }

    private static bool IpMatches(string filterIp, string? eventIp)
    {
        if (eventIp == null) return false;
        if (filterIp.Contains('/'))
            return NetworkUtilities.IsIpInSubnet(eventIp, filterIp);
        return string.Equals(filterIp, eventIp, StringComparison.Ordinal);
    }
}

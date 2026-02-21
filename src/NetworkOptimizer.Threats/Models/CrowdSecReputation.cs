namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Cached CrowdSec reputation data for an IP address.
/// </summary>
public class CrowdSecReputation
{
    /// <summary>
    /// IP address (primary key).
    /// </summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>
    /// Raw JSON response from CrowdSec CTI API.
    /// </summary>
    public string ReputationJson { get; set; } = "{}";

    /// <summary>
    /// When this data was fetched.
    /// </summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// When this cache entry expires.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}

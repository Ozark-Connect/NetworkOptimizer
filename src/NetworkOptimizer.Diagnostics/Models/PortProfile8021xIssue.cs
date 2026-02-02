namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Identifies port profiles configured for trunk/AP use that have 802.1X Control
/// set to "Auto", which will cause network fabric connectivity loss when 802.1X is enabled.
/// </summary>
public class PortProfile8021xIssue
{
    /// <summary>
    /// ID of the port profile with the issue
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the port profile
    /// </summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Current 802.1X control setting (typically "auto")
    /// </summary>
    public string CurrentDot1xCtrl { get; set; } = string.Empty;

    /// <summary>
    /// Number of tagged VLANs on this profile (or -1 if "Allow All")
    /// </summary>
    public int TaggedVlanCount { get; set; }

    /// <summary>
    /// Whether this profile uses "Allow All" tagged VLANs
    /// </summary>
    public bool AllowsAllVlans { get; set; }

    /// <summary>
    /// Human-readable recommendation
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;
}

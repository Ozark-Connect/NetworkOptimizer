namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Classifies a noise filter to control how matched events surface in reports
/// and dashboards. All categories still exclude events from BaseQuery, but the
/// audit PDF surfaces Infrastructure and TrustedUser in separate categorized
/// sub-tables so the user can see what was suppressed and why, instead of having
/// it silently disappear from the threat table.
/// </summary>
public enum ThreatFilterCategory
{
    /// <summary>
    /// Generic noise. Hidden everywhere with no further accounting.
    /// </summary>
    Noise = 0,

    /// <summary>
    /// Known infrastructure (the optimizer LXC itself, local DNS proxies, etc.).
    /// Hidden from the top threat sources table, surfaced separately as
    /// "Known Infrastructure Activity" with event counts and labels.
    /// </summary>
    Infrastructure = 1,

    /// <summary>
    /// Trusted user devices (workstations, phones). These generate recon-class
    /// events from normal browsing and discovery. Hidden from the top threat
    /// sources table, surfaced separately as "Trusted User Activity".
    /// </summary>
    TrustedUser = 2
}

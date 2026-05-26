namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// Records changes the threat collector makes on its own as system entries in the audit log.
/// Implemented by the web host, which owns the audit log; must never throw or block.
/// </summary>
public interface IThreatSystemAudit
{
    /// <summary>
    /// A noise filter was created, demoted, or promoted without a user action.
    /// </summary>
    /// <param name="change">Short machine-readable change name, e.g. "self_filter_created".</param>
    /// <param name="sourceIp">The filter's source IP.</param>
    /// <param name="siteSlug">The site whose filter changed; null for the default site.</param>
    /// <param name="reason">Why the collector made the change.</param>
    void NoiseFilterChanged(string change, string sourceIp, string? siteSlug, string reason);
}

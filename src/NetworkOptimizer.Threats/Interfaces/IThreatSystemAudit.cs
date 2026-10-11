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

    /// <summary>Automatic Blocking blocked a source on a site (Enforce mode only; null site = default).</summary>
    void SourceBlocked(string? siteSlug, string sourceIp, string reason, DateTime expiresAt, IReadOnlyList<string> targets);

    /// <summary>A site's block ended on its own and its source was removed from the targets.</summary>
    void BlockExpired(string? siteSlug, string sourceIp);

    /// <summary>A site's observed addresses changed in Trusted Sources: new ones seen, replaced ones aged out.</summary>
    void TrustedAddressesChanged(string? siteSlug, IReadOnlyList<string> added, IReadOnlyList<string> dropped);
}

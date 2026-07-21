namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Per-site role. Numeric values are ordered by privilege (Viewer &lt; Operator &lt; Admin) so
/// effective-role resolution can take a simple <c>max()</c> across all applicable grants
/// (design doc 04).
/// </summary>
public enum SiteRole
{
    /// <summary>Read-only on the site: dashboards, monitoring, results, reports.</summary>
    SiteViewer = 0,

    /// <summary>Operate the site: speed tests, audits, SQM apply, optimizer apply.</summary>
    SiteOperator = 1,

    /// <summary>Site-scoped operate + site settings (monitoring setup, agent enrollment, rename) + manage the site's memberships. Not global settings.</summary>
    SiteAdmin = 2,
}

/// <summary>
/// What a <see cref="SiteMembership"/> row grants access to.
/// </summary>
public enum MembershipTargetType
{
    /// <summary>A single site, identified by slug in <see cref="SiteMembership.TargetId"/>.</summary>
    Site = 0,

    /// <summary>A <see cref="SiteGroup"/> (its id in <see cref="SiteMembership.TargetId"/>); unions to every site in the group.</summary>
    Group = 1,

    /// <summary>Every site (NOC pattern); <see cref="SiteMembership.TargetId"/> is null.</summary>
    AllSites = 2,
}

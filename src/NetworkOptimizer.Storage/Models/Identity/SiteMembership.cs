namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// A per-site (or per-group, or all-sites) role grant for a user. Our table, not one of Identity's.
/// Effective site role for a user is the max over their global-role-implied level and every
/// applicable membership row: direct site, group-derived, and AllSites wildcard (design doc 04).
/// </summary>
public class SiteMembership
{
    public int Id { get; set; }

    /// <summary>The <see cref="ApplicationUser"/> this grant belongs to.</summary>
    public string UserId { get; set; } = "";

    /// <summary>Whether this grant targets a single site, a group of sites, or all sites.</summary>
    public MembershipTargetType TargetType { get; set; }

    /// <summary>
    /// Site slug (<see cref="MembershipTargetType.Site"/>), <see cref="SiteGroup.Id"/> as a string
    /// (<see cref="MembershipTargetType.Group"/>), or null (<see cref="MembershipTargetType.AllSites"/>).
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>The site-scoped role granted on the target.</summary>
    public SiteRole SiteRole { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

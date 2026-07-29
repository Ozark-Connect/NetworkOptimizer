namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// A named collection of sites (e.g. "region-west", "tier-gold") used to make RBAC scale past the
/// point where explicit per-site rows stop working (design doc 04, enterprise phase). A
/// <see cref="SiteMembership"/> may target a group; adding a site to the group at onboarding grants
/// every group-targeted membership instantly, fixing the "site #1001 problem".
/// </summary>
public class SiteGroup
{
    public int Id { get; set; }

    /// <summary>Unique group name.</summary>
    public string Name { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Membership of a site (by slug) in a <see cref="SiteGroup"/>. Assigned at site
/// creation/onboarding and editable in Settings.
/// </summary>
public class SiteGroupMember
{
    public int Id { get; set; }

    /// <summary><see cref="SiteGroup.Id"/>.</summary>
    public int GroupId { get; set; }

    /// <summary>The member site's immutable slug.</summary>
    public string SiteSlug { get; set; } = "";
}

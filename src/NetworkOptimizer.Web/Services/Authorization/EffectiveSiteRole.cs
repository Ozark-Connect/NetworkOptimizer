using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>A single membership grant flattened for effective-role computation.</summary>
public readonly record struct MembershipGrant(MembershipTargetType TargetType, string? TargetId, SiteRole SiteRole);

/// <summary>
/// Pure effective-role computation (design doc 04): the effective site role is the max over the role
/// implied by the global role and every applicable membership (direct site, group-derived, AllSites
/// wildcard). Kept side-effect-free so the full role matrix is unit-tested without a database.
/// </summary>
public static class EffectiveSiteRole
{
    /// <summary>
    /// Computes the effective site role for a user on <paramref name="slug"/>, or null when the user
    /// has no access to that site.
    /// </summary>
    /// <param name="isGlobalAdmin">Whether the user holds the global Admin role (implies SiteAdmin everywhere).</param>
    /// <param name="globalImplied">Site role implied by a global Operator/Viewer role, or null.</param>
    /// <param name="memberships">The user's flattened membership grants.</param>
    /// <param name="slug">The target site slug.</param>
    /// <param name="slugInGroup">Predicate: does <paramref name="slug"/> belong to the given group id?</param>
    /// <param name="restrictSitesToMembers">When true, the global-implied role does NOT grant blanket site access.</param>
    public static SiteRole? Compute(
        bool isGlobalAdmin,
        SiteRole? globalImplied,
        IEnumerable<MembershipGrant> memberships,
        string slug,
        Func<string, bool> slugInGroup,
        bool restrictSitesToMembers)
    {
        // Global Admin is SiteAdmin everywhere and bypasses the restriction toggle.
        if (isGlobalAdmin)
            return SiteRole.SiteAdmin;

        SiteRole? best = null;
        foreach (var m in memberships)
        {
            var applies = m.TargetType switch
            {
                MembershipTargetType.AllSites => true,
                MembershipTargetType.Site => string.Equals(m.TargetId, slug, StringComparison.OrdinalIgnoreCase),
                MembershipTargetType.Group => m.TargetId is not null && slugInGroup(m.TargetId),
                _ => false,
            };
            if (applies)
                best = Max(best, m.SiteRole);
        }

        // A global Operator/Viewer role applies across all sites only when restriction is off.
        if (!restrictSitesToMembers && globalImplied is not null)
            best = Max(best, globalImplied.Value);

        return best;
    }

    /// <summary>The site role implied by a global role, or null for Admin (handled separately) / none.</summary>
    public static SiteRole? GlobalImplied(bool isOperator, bool isViewer)
        => isOperator ? SiteRole.SiteOperator
            : isViewer ? SiteRole.SiteViewer
            : null;

    /// <summary>Returns the higher-privileged of two site roles.</summary>
    public static SiteRole Max(SiteRole? current, SiteRole candidate)
        => current is null ? candidate : (SiteRole)Math.Max((int)current.Value, (int)candidate);
}

using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>Where an account's effective role on a site comes from.</summary>
public enum AccessSource
{
    /// <summary>A membership grant is the highest thing that applies.</summary>
    Grant,

    /// <summary>The global Operator or Viewer role, which applies everywhere while the site restriction is off.</summary>
    GlobalRole,

    /// <summary>Global Admin, which is Site Admin everywhere regardless of grants or the restriction.</summary>
    GlobalAdmin,
}

/// <summary>
/// One line of the Access list: who can access a target and at what role, with the grant behind it
/// where there is one. <see cref="MembershipId"/> is null for a row that exists only because of a
/// global role, so there is nothing to revoke.
/// </summary>
public sealed record AccessEntry(
    string UserId,
    string? UserName,
    MembershipTargetType TargetType,
    string? TargetId,
    SiteRole EffectiveRole,
    AccessSource Source,
    int? MembershipId,
    SiteRole? GrantRole);

/// <summary>One account as the Access computation sees it.</summary>
public sealed record AccessUser(
    string UserId,
    string? UserName,
    string? GlobalRole,
    IReadOnlyList<SiteMembership> Memberships);

/// <summary>
/// The Access list as effective access rather than a list of grants. A grant list leaves out everyone
/// who gets in through a global role and misreports a grant the global role already exceeds, so the
/// rows here carry the role that actually applies and name what it comes from.
/// </summary>
public static class EffectiveAccess
{
    /// <summary>Everyone who can access <paramref name="slug"/>, one entry each.</summary>
    public static List<AccessEntry> ForSite(
        IEnumerable<AccessUser> users,
        string slug,
        Func<string, bool> slugInGroup,
        bool restrictSitesToMembers)
    {
        var rows = new List<AccessEntry>();
        foreach (var user in users)
        {
            var isAdmin = user.GlobalRole == Roles.Admin;
            var implied = EffectiveSiteRole.GlobalImplied(
                user.GlobalRole == Roles.Operator, user.GlobalRole == Roles.Viewer);

            var grants = user.Memberships.Select(m => new MembershipGrant(m.TargetType, m.TargetId, m.SiteRole));
            var effective = EffectiveSiteRole.Compute(isAdmin, implied, grants, slug, slugInGroup, restrictSitesToMembers);
            if (effective is null)
                continue;

            // The grant that carries the row: the highest that covers the site, a direct one ahead of a
            // wider one at the same role because it is the one a Site Admin can revoke.
            var best = user.Memberships
                .Where(m => Covers(m, slug, slugInGroup))
                .OrderByDescending(m => (int)m.SiteRole)
                .ThenBy(m => m.TargetType == MembershipTargetType.Site ? 0 : m.TargetType == MembershipTargetType.AllSites ? 1 : 2)
                .FirstOrDefault();

            rows.Add(new AccessEntry(
                user.UserId, user.UserName, MembershipTargetType.Site, slug,
                effective.Value, SourceOf(isAdmin, best?.SiteRole, effective.Value),
                best?.Id, best?.SiteRole));
        }

        return rows;
    }

    /// <summary>
    /// The instance-wide list: every grant with the role that applies on its target, plus an
    /// "All sites" row for each account whose global role gets it in without one.
    /// </summary>
    public static List<AccessEntry> Overview(IEnumerable<AccessUser> users, bool restrictSitesToMembers)
    {
        var rows = new List<AccessEntry>();
        foreach (var user in users)
        {
            var isAdmin = user.GlobalRole == Roles.Admin;
            var implied = EffectiveSiteRole.GlobalImplied(
                user.GlobalRole == Roles.Operator, user.GlobalRole == Roles.Viewer);
            var appliesEverywhere = isAdmin || (!restrictSitesToMembers && implied is not null);

            foreach (var m in user.Memberships)
            {
                var effective = isAdmin
                    ? SiteRole.SiteAdmin
                    : appliesEverywhere ? EffectiveSiteRole.Max(implied, m.SiteRole) : m.SiteRole;
                rows.Add(new AccessEntry(
                    user.UserId, user.UserName, m.TargetType, m.TargetId,
                    effective, SourceOf(isAdmin, m.SiteRole, effective), m.Id, m.SiteRole));
            }

            // An All sites grant already says everything this row would; anything narrower does not.
            if (appliesEverywhere && user.Memberships.All(m => m.TargetType != MembershipTargetType.AllSites))
            {
                rows.Add(new AccessEntry(
                    user.UserId, user.UserName, MembershipTargetType.AllSites, null,
                    isAdmin ? SiteRole.SiteAdmin : implied!.Value,
                    isAdmin ? AccessSource.GlobalAdmin : AccessSource.GlobalRole,
                    null, null));
            }
        }

        return rows;
    }

    private static AccessSource SourceOf(bool isAdmin, SiteRole? grant, SiteRole effective)
    {
        if (isAdmin) return AccessSource.GlobalAdmin;
        return grant == effective ? AccessSource.Grant : AccessSource.GlobalRole;
    }

    private static bool Covers(SiteMembership m, string slug, Func<string, bool> slugInGroup) => m.TargetType switch
    {
        MembershipTargetType.AllSites => true,
        MembershipTargetType.Site => string.Equals(m.TargetId, slug, StringComparison.OrdinalIgnoreCase),
        MembershipTargetType.Group => m.TargetId is not null && slugInGroup(m.TargetId),
        _ => false,
    };
}

using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Resolves a user's effective site role and their authorized-site slug set from the local user id
/// plus their memberships/groups (design doc 04). The authorized-slug set is cached per
/// (user, membership-version) so a 1000-site fleet costs one set build, not a per-site query; the
/// cache invalidates automatically when the user's membership version advances.
/// </summary>
public interface IEffectiveSiteRoleResolver
{
    /// <summary>Effective site role for the principal on <paramref name="slug"/>, or null for no access.</summary>
    Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug);

    /// <summary>The set of site slugs the principal may at least view (used for site-context filtering).</summary>
    Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user);
}

/// <inheritdoc />
public sealed class EffectiveSiteRoleResolver : IEffectiveSiteRoleResolver
{
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly IAuthPolicyOptions _policy;
    private readonly IMemoryCache _cache;

    public EffectiveSiteRoleResolver(
        IDbContextFactory<AuthDbContext> authDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        IAuthPolicyOptions policy,
        IMemoryCache cache)
    {
        _authDbFactory = authDbFactory;
        _mainDbFactory = mainDbFactory;
        _policy = policy;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return null;

        if (user.IsInRole(Roles.Admin))
            return SiteRole.SiteAdmin;

        // Cached on the same membership-version key as the authorized-slug set: this is now the
        // capability check for every site-scoped gated call, so an uncached read here is a database
        // round trip on every deploy, adjustment, scan, and speed test. A membership change bumps the
        // version and invalidates it, which is the same contract the slug set already relies on.
        var membershipVersion = user.FindFirstValue(NetOptClaims.MembershipVersion) ?? "0";
        var cacheKey = $"siterole:{userId}:{membershipVersion}:{slug}";
        if (_cache.TryGetValue(cacheKey, out SiteRole? cached))
            return cached;

        var role = await ComputeEffectiveRoleAsync(user, userId, slug);
        _cache.Set(cacheKey, role, TimeSpan.FromMinutes(10));
        return role;
    }

    private async Task<SiteRole?> ComputeEffectiveRoleAsync(ClaimsPrincipal user, string userId, string slug)
    {
        var (memberships, groupSlugs) = await LoadMembershipsAsync(userId);
        var restrict = await _policy.IsRestrictSitesToMembersAsync();
        var globalImplied = EffectiveSiteRole.GlobalImplied(
            user.IsInRole(Roles.Operator), user.IsInRole(Roles.Viewer));

        return EffectiveSiteRole.Compute(
            isGlobalAdmin: false,
            globalImplied,
            memberships,
            slug,
            slugInGroup: groupId => groupSlugs.TryGetValue(groupId, out var slugs) && slugs.Contains(slug),
            restrictSitesToMembers: restrict);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return new HashSet<string>();

        var membershipVersion = user.FindFirstValue(NetOptClaims.MembershipVersion) ?? "0";
        var cacheKey = $"authslugs:{userId}:{membershipVersion}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlySet<string>? cached) && cached is not null)
            return cached;

        var result = await BuildAuthorizedSlugsAsync(user, userId);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
        return result;
    }

    private async Task<IReadOnlySet<string>> BuildAuthorizedSlugsAsync(ClaimsPrincipal user, string userId)
    {
        var allSlugs = await LoadAllSlugsAsync();

        // Admin, and (when unrestricted) any global role, see every site.
        if (user.IsInRole(Roles.Admin))
            return allSlugs;
        if (!await _policy.IsRestrictSitesToMembersAsync())
            return allSlugs;

        var (memberships, groupSlugs) = await LoadMembershipsAsync(userId);
        var authorized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in memberships)
        {
            switch (m.TargetType)
            {
                case MembershipTargetType.AllSites:
                    return allSlugs;
                case MembershipTargetType.Site when m.TargetId is not null:
                    authorized.Add(m.TargetId);
                    break;
                case MembershipTargetType.Group when m.TargetId is not null
                        && groupSlugs.TryGetValue(m.TargetId, out var slugs):
                    authorized.UnionWith(slugs);
                    break;
            }
        }
        return authorized;
    }

    private async Task<(List<MembershipGrant> Memberships, Dictionary<string, HashSet<string>> GroupSlugs)>
        LoadMembershipsAsync(string userId)
    {
        await using var authDb = await _authDbFactory.CreateDbContextAsync();

        var memberships = await authDb.SiteMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new MembershipGrant(m.TargetType, m.TargetId, m.SiteRole))
            .ToListAsync();

        // Only expand groups the user actually derives access from.
        var neededGroupIds = memberships
            .Where(m => m.TargetType == MembershipTargetType.Group && m.TargetId is not null)
            .Select(m => int.Parse(m.TargetId!))
            .ToHashSet();

        var groupSlugs = new Dictionary<string, HashSet<string>>();
        if (neededGroupIds.Count > 0)
        {
            var members = await authDb.SiteGroupMembers
                .AsNoTracking()
                .Where(gm => neededGroupIds.Contains(gm.GroupId))
                .Select(gm => new { gm.GroupId, gm.SiteSlug })
                .ToListAsync();
            foreach (var gm in members)
            {
                if (!groupSlugs.TryGetValue(gm.GroupId.ToString(), out var set))
                    groupSlugs[gm.GroupId.ToString()] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(gm.SiteSlug);
            }
        }

        return (memberships, groupSlugs);
    }

    private async Task<IReadOnlySet<string>> LoadAllSlugsAsync()
    {
        await using var mainDb = await _mainDbFactory.CreateDbContextAsync();
        var slugs = await mainDb.Sites.AsNoTracking().Select(s => s.Slug).ToListAsync();
        return new HashSet<string>(slugs, StringComparer.OrdinalIgnoreCase);
    }
}

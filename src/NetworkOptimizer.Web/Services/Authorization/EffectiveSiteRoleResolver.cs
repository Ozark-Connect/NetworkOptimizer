using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Resolves a user's effective site role and their authorized-site slug set from the local user id
/// plus their memberships/groups (design doc 04). The authorized-slug set is cached per user so a
/// 1000-site fleet costs one set build, not a per-site query; changing that user's memberships or
/// roles drops the entries through <see cref="IEffectiveSiteRoleResolver.Invalidate"/>.
/// </summary>
public interface IEffectiveSiteRoleResolver
{
    /// <summary>Effective site role for the principal on <paramref name="slug"/>, or null for no access.</summary>
    Task<SiteRole?> GetEffectiveRoleAsync(ClaimsPrincipal user, string slug);

    /// <summary>The set of site slugs the principal may at least view (used for site-context filtering).</summary>
    Task<IReadOnlySet<string>> GetAuthorizedSlugsAsync(ClaimsPrincipal user);

    /// <summary>
    /// A site the principal administers, or null if there is none. Answers both "should this person
    /// be offered the settings they can reach somewhere" and "which site do those settings open on",
    /// without asking site by site - a fleet-sized install cannot afford the loop.
    /// </summary>
    Task<string?> FirstAdministeredSlugAsync(ClaimsPrincipal user);

    /// <summary>
    /// Drops everything cached for one user, so a membership or role change applies to their very
    /// next action instead of at the end of the cache window.
    /// </summary>
    void Invalidate(string userId);

    /// <summary>
    /// Drops every user's cached resolution. The site list is baked into the authorized-slug set, so
    /// a site that has just been added exists in no cached set - which hides it from everyone, an
    /// Admin included, until the entry ages out.
    /// </summary>
    void InvalidateAll();
}

/// <inheritdoc />
public sealed class EffectiveSiteRoleResolver : IEffectiveSiteRoleResolver
{
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly IAuthPolicyOptions _policy;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EffectiveSiteRoleResolver>? _logger;

    public EffectiveSiteRoleResolver(
        IDbContextFactory<AuthDbContext> authDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        IAuthPolicyOptions policy,
        IMemoryCache cache,
        ILogger<EffectiveSiteRoleResolver>? logger = null)
    {
        _authDbFactory = authDbFactory;
        _mainDbFactory = mainDbFactory;
        _policy = policy;
        _cache = cache;
        _logger = logger;
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
        var cacheKey = $"siterole:{userId}:{slug}";
        if (_cache.TryGetValue(cacheKey, out SiteRole? cached))
            return cached;

        var role = await ComputeEffectiveRoleAsync(user, userId, slug);
        _cache.Set(cacheKey, role, EntryOptions(userId));
        return role;
    }

    /// <inheritdoc />
    public async Task<string?> FirstAdministeredSlugAsync(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return null;

        // A global Admin administers every site, so the answer is simply the first one there is.
        if (user.IsInRole(Roles.Admin))
            return (await GetAuthorizedSlugsAsync(user)).FirstOrDefault();

        var cacheKey = $"firstadministered:{userId}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached;

        var (memberships, groupSlugs) = await LoadMembershipsAsync(userId);
        string? slug = null;
        foreach (var m in memberships.Where(m => m.SiteRole == SiteRole.SiteAdmin))
        {
            slug = m.TargetType switch
            {
                MembershipTargetType.Site => m.TargetId,
                MembershipTargetType.Group when m.TargetId is not null
                    && groupSlugs.TryGetValue(m.TargetId, out var slugs) => slugs.FirstOrDefault(),
                MembershipTargetType.AllSites => (await GetAuthorizedSlugsAsync(user)).FirstOrDefault(),
                _ => null,
            };
            if (slug is not null)
                break;
        }

        _cache.Set(cacheKey, slug, EntryOptions(userId));
        return slug;
    }

    /// <inheritdoc />
    public void Invalidate(string userId)
    {
        if (!_cache.TryGetValue(TokenKey(userId), out CancellationTokenSource? source) || source is null)
            return;

        // Cancelling expires every entry that carries this token, which is all of this user's.
        _cache.Remove(TokenKey(userId));
        source.Cancel();
        source.Dispose();
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        if (!_cache.TryGetValue(RegistryTokenKey, out CancellationTokenSource? source) || source is null)
            return;

        _cache.Remove(RegistryTokenKey);
        source.Cancel();
        source.Dispose();
    }

    private static string TokenKey(string userId) => $"siterole-token:{userId}";

    private const string RegistryTokenKey = "siterole-token:site-registry";

    /// <summary>
    /// Ties an entry to its user's cancellation token as well as the ten-minute window, so
    /// <see cref="Invalidate"/> can drop them all without knowing the site slugs they cover.
    /// </summary>
    private MemoryCacheEntryOptions EntryOptions(string userId)
    {
        return new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(10))
            .AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(Token(TokenKey(userId))))
            .AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(Token(RegistryTokenKey)));
    }

    private CancellationToken Token(string key)
    {
        var source = _cache.GetOrCreate(key, entry =>
        {
            entry.Priority = CacheItemPriority.NeverRemove;
            return new CancellationTokenSource();
        })!;
        return source.Token;
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

        var cacheKey = $"authslugs:{userId}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlySet<string>? cached) && cached is not null)
            return cached;

        var result = await BuildAuthorizedSlugsAsync(user, userId);
        _cache.Set(cacheKey, result, EntryOptions(userId));
        _logger?.LogDebug("Authorized sites rebuilt for {UserId} (admin={IsAdmin}, restrict={Restrict}): {Slugs}",
            userId, user.IsInRole(Roles.Admin), await _policy.IsRestrictSitesToMembersAsync(),
            string.Join(",", result));
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

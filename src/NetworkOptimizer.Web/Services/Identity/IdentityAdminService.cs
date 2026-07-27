using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>One user as the Identity tab lists them: identity, access, and credential facts in one row.</summary>
/// <param name="User">The user record.</param>
/// <param name="GlobalRole">Highest global role held, or null when the account has none.</param>
/// <param name="HasPassword">True when a local password is set; false for a federated-only account.</param>
/// <param name="MfaEnabled">True when an authenticator app is enrolled.</param>
/// <param name="PasskeyCount">Number of registered passkeys.</param>
/// <param name="LinkedProviders">Scheme names of linked external identity providers.</param>
public sealed record UserAccountSummary(
    ApplicationUser User,
    string? GlobalRole,
    bool HasPassword,
    bool MfaEnabled,
    int PasskeyCount,
    IReadOnlyList<string> LinkedProviders,
    bool HasSiteAccess);

/// <summary>An external identity linked to a local account.</summary>
/// <param name="LoginProvider">Provider scheme (matches <see cref="FederationProvider.Scheme"/>).</param>
/// <param name="ProviderKey">The IdP-side subject identifier.</param>
/// <param name="DisplayName">Provider display name shown in the UI.</param>
public sealed record LinkedExternalIdentity(string LoginProvider, string ProviderKey, string? DisplayName);

/// <summary>One user's granted access to a single site.</summary>
/// <param name="MembershipId">Row id, used to revoke the grant.</param>
/// <param name="UserId">The user the grant belongs to.</param>
/// <param name="UserName">Username snapshot for display.</param>
/// <param name="SiteRole">The role granted on the site.</param>
public sealed record SiteAccessGrant(int MembershipId, string UserId, string? UserName, SiteRole SiteRole);

/// <summary>Whether a global role's members must enrol in MFA.</summary>
/// <param name="Role">Global role name.</param>
/// <param name="RequireMfa">True when members of the role must enrol before they can use the app.</param>
public sealed record RoleMfaPolicy(string Role, bool RequireMfa);

/// <summary>Result of an identity-admin mutation; carries a user-facing error for refusals (e.g. last-admin).</summary>
public sealed record AdminActionResult(bool Succeeded, string? Error = null)
{
    public static AdminActionResult Ok() => new(true);
    public static AdminActionResult Fail(string error) => new(false, error);
}

/// <summary>
/// The ONLY type that touches <see cref="UserManager{TUser}"/>, the role store, and the membership/
/// group tables (design doc 06, gate 10 - enforced by architecture test A3). Every mutation audits,
/// rotates the security stamp / bumps the membership version so sessions revalidate, and upholds the
/// last-admin and site-ownership invariants (design doc 04). Callers never manipulate Identity directly.
/// </summary>
public interface IIdentityAdminService
{
    Task<IReadOnlyList<ApplicationUser>> ListUsersAsync();
    Task<int> CountUsersAsync();
    Task<ApplicationUser?> FindByIdAsync(string userId);
    Task<IReadOnlyList<string>> GetGlobalRolesAsync(ApplicationUser user);

    /// <summary>
    /// Every user with the credential and access facts the Identity tab lists: global role, whether
    /// they are enabled, MFA and passkey enrolment, and any linked external identities.
    /// </summary>
    Task<IReadOnlyList<UserAccountSummary>> ListUserSummariesAsync();

    /// <summary>External identities linked to one user (provider scheme plus the IdP-side subject).</summary>
    Task<IReadOnlyList<LinkedExternalIdentity>> GetExternalLoginsAsync(string userId);

    /// <summary>Links an external identity to a local user on an admin's behalf.</summary>
    Task<AdminActionResult> LinkExternalAsync(string userId, string loginProvider, string providerKey, string? displayName);

    /// <summary>Removes a linked external identity (the user keeps any local password).</summary>
    Task<AdminActionResult> UnlinkExternalAsync(string userId, string loginProvider, string providerKey);

    /// <param name="siteTarget">
    /// Site the new account is granted, at the role its global role implies: a slug, <c>"*"</c> for all
    /// sites, or null to grant nothing. Granting here rather than in a second step matters because an
    /// install with users is usually running many sites, where scoping the account IS the task.
    /// Ignored for Admin, which reaches every site regardless.
    /// </param>
    Task<AdminActionResult> CreateUserAsync(
        string username, string? displayName, string? password, string globalRole, string? siteTarget = null);
    Task<AdminActionResult> SetEnabledAsync(string userId, bool enabled);
    Task<AdminActionResult> DeleteUserAsync(string userId);
    Task<AdminActionResult> SetPasswordAsync(string userId, string newPassword);

    /// <summary>
    /// Sets the signed-in user's own password. This is what the Admin Password control in Settings
    /// drives, so a single-admin install changes its password where it always has and the change is
    /// what authenticates at the next sign-in.
    /// </summary>
    Task<AdminActionResult> SetOwnPasswordAsync(string userId, string newPassword);

    /// <summary>
    /// Changes the signed-in user's own password, proving the current one first. Distinct from
    /// <see cref="SetOwnPasswordAsync"/>, which an admin path uses without that proof: self-service
    /// has to establish that whoever is at the keyboard is the account holder and not someone who
    /// found it unlocked.
    /// </summary>
    Task<AdminActionResult> ChangeOwnPasswordAsync(string userId, string currentPassword, string newPassword);

    /// <summary>
    /// Rotates the signed-in user's security stamp, which every application cookie and remembered
    /// two-factor cookie is validated against - so every session for the account stops being valid.
    /// The browser that asked keeps its access only because the caller re-issues that one cookie.
    /// </summary>
    Task<AdminActionResult> SignOutEverywhereAsync(string userId);

    Task<AdminActionResult> GrantGlobalRoleAsync(string userId, string role);
    Task<AdminActionResult> RevokeGlobalRoleAsync(string userId, string role);

    Task<IReadOnlyList<SiteMembership>> GetMembershipsAsync(string userId);

    /// <summary>
    /// Who can reach one site and at what role: the direct memberships on that slug, used by the
    /// per-site Identity tab's Access list.
    /// </summary>
    Task<IReadOnlyList<SiteAccessGrant>> GetSiteAccessAsync(string siteSlug);

    Task<AdminActionResult> AddMembershipAsync(string userId, MembershipTargetType targetType, string? targetId, SiteRole role);
    Task<AdminActionResult> RemoveMembershipAsync(string userId, int membershipId);

    /// <summary>Per-role "require MFA" policy, in role-privilege order.</summary>
    Task<IReadOnlyList<RoleMfaPolicy>> GetRoleMfaPoliciesAsync();

    /// <summary>Turns the "require MFA" policy on or off for one global role.</summary>
    Task<AdminActionResult> SetRoleRequiresMfaAsync(string role, bool requireMfa);

    Task<IReadOnlyList<SiteGroup>> GetSiteGroupsAsync();
    Task<AdminActionResult> CreateSiteGroupAsync(string name);
    Task<AdminActionResult> DeleteSiteGroupAsync(int groupId);
    Task<AdminActionResult> SetSiteGroupMembersAsync(int groupId, IReadOnlyCollection<string> siteSlugs);
}

/// <inheritdoc />
public sealed class IdentityAdminService : IIdentityAdminService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;
    private readonly Authorization.IEffectiveSiteRoleResolver _siteRoles;
    private readonly ILogger<IdentityAdminService> _logger;

    public IdentityAdminService(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IDbContextFactory<AuthDbContext> authDbFactory,
        IAuditLogger audit,
        ICallerContext caller,
        Authorization.IEffectiveSiteRoleResolver siteRoles,
        ILogger<IdentityAdminService> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _authDbFactory = authDbFactory;
        _audit = audit;
        _caller = caller;
        _siteRoles = siteRoles;
        _logger = logger;
    }

    /// <summary>
    /// The site-ownership invariant: a membership may only be changed by someone who owns the site it
    /// targets. A global Admin owns every site; a SiteAdmin owns exactly the one site they administer,
    /// so they can never grant AllSites, a group (which spans sites), or anything on another site.
    /// The scoped Access card already narrows the target in the UI, but a Blazor circuit's bound values
    /// arrive from the browser, so the UI cannot be the thing enforcing this.
    /// Returns null when the change is allowed.
    /// </summary>
    private async Task<AdminActionResult?> RefuseUnlessOwnedAsync(MembershipTargetType targetType, string? targetId)
    {
        var caller = _caller.Current;

        // Bootstrap, background work, and installs with authentication off have no principal to scope
        // by, and have always been able to do everything locally.
        if (caller is null || caller.IsSystem || caller.AuthenticationDisabled || caller.Principal is null)
            return null;

        if (caller.Principal.IsInRole(Roles.Admin))
            return null;

        if (targetType != MembershipTargetType.Site || string.IsNullOrEmpty(targetId))
            return AdminActionResult.Fail("Only an Admin can grant access across sites.");

        var role = await _siteRoles.GetEffectiveRoleAsync(caller.Principal, targetId);
        return role == SiteRole.SiteAdmin
            ? null
            : AdminActionResult.Fail("You can only change access on a site you administer.");
    }

    public async Task<IReadOnlyList<ApplicationUser>> ListUsersAsync()
        => await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();

    public Task<int> CountUsersAsync() => _userManager.Users.CountAsync();

    public Task<ApplicationUser?> FindByIdAsync(string userId) => _userManager.FindByIdAsync(userId)!;

    public async Task<IReadOnlyList<string>> GetGlobalRolesAsync(ApplicationUser user)
        => (await _userManager.GetRolesAsync(user)).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserAccountSummary>> ListUserSummariesAsync()
    {
        var users = await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();
        var summaries = new List<UserAccountSummary>(users.Count);

        // Which accounts hold any grant at all, in one query rather than per user. With the site
        // restriction on, a non-Admin holding none can reach nothing, which is worth surfacing in the
        // list instead of leaving them to discover it through empty pages.
        HashSet<string> withGrants;
        await using (var db = await _authDbFactory.CreateDbContextAsync())
        {
            withGrants = (await db.SiteMemberships
                .AsNoTracking()
                .Select(m => m.UserId)
                .Distinct()
                .ToListAsync()).ToHashSet(StringComparer.Ordinal);
        }

        foreach (var user in users)
        {
            var roles = await _userManager.GetRolesAsync(user);
            var logins = await _userManager.GetLoginsAsync(user);
            var passkeys = await _userManager.GetPasskeysAsync(user);
            var globalRole = Roles.All.FirstOrDefault(roles.Contains);
            summaries.Add(new UserAccountSummary(
                user,
                globalRole,
                await _userManager.HasPasswordAsync(user),
                await _userManager.GetTwoFactorEnabledAsync(user),
                passkeys.Count,
                logins.Select(l => l.LoginProvider).ToList(),
                HasSiteAccess: globalRole == Roles.Admin || withGrants.Contains(user.Id)));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedExternalIdentity>> GetExternalLoginsAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return Array.Empty<LinkedExternalIdentity>();

        var logins = await _userManager.GetLoginsAsync(user);
        return logins
            .Select(l => new LinkedExternalIdentity(l.LoginProvider, l.ProviderKey, l.ProviderDisplayName))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> LinkExternalAsync(string userId, string loginProvider, string providerKey, string? displayName)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        var existing = await _userManager.FindByLoginAsync(loginProvider, providerKey);
        if (existing is not null)
        {
            return existing.Id == userId
                ? AdminActionResult.Ok()
                : AdminActionResult.Fail($"That identity is already linked to '{existing.UserName}'.");
        }

        var result = await _userManager.AddLoginAsync(user, new UserLoginInfo(loginProvider, providerKey, displayName));
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        Emit(AuditCategories.User, AuditActions.ExternalLinked, user, new { loginProvider });
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> UnlinkExternalAsync(string userId, string loginProvider, string providerKey)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        var result = await _userManager.RemoveLoginAsync(user, loginProvider, providerKey);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        await _userManager.UpdateSecurityStampAsync(user);
        Emit(AuditCategories.User, AuditActions.ExternalUnlinked, user, new { loginProvider });
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> ChangeOwnPasswordAsync(string userId, string currentPassword, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        // Nothing to prove against, and no password to replace - a passkey or federated account gets
        // one set elsewhere, not changed here.
        if (!await _userManager.HasPasswordAsync(user))
            return AdminActionResult.Fail("This account signs in without a password.");

        if (await _userManager.CheckPasswordAsync(user, newPassword))
            return AdminActionResult.Fail("The new password is the one already in use.");

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
            return AdminActionResult.Fail(Describe(result));

        user.PasswordIsTemporary = false;
        await _userManager.UpdateAsync(user);
        Emit(AuditCategories.User, AuditActions.PasswordReset, user, new { self = true });
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> SignOutEverywhereAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        await _userManager.UpdateSecurityStampAsync(user);
        Emit(AuditCategories.Auth, AuditActions.SignedOutEverywhere, user, new { self = true });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetOwnPasswordAsync(string userId, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        // A federated-only account (or one carried over from an install that never had a local
        // password) has no hash to reset, so the change becomes an add.
        var result = await _userManager.HasPasswordAsync(user)
            ? await _userManager.ResetPasswordAsync(user, await _userManager.GeneratePasswordResetTokenAsync(user), newPassword)
            : await _userManager.AddPasswordAsync(user, newPassword);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        if (user.PasswordIsTemporary)
        {
            user.PasswordIsTemporary = false;
            await _userManager.UpdateAsync(user);
        }

        Emit(AuditCategories.Auth, AuditActions.PasswordChanged, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> CreateUserAsync(
        string username, string? displayName, string? password, string globalRole, string? siteTarget = null)
    {
        if (!Roles.All.Contains(globalRole))
            return AdminActionResult.Fail($"Unknown role '{globalRole}'.");

        var user = new ApplicationUser
        {
            UserName = username,
            DisplayName = displayName,
            IsEnabled = true,
        };

        var create = string.IsNullOrEmpty(password)
            ? await _userManager.CreateAsync(user)
            : await _userManager.CreateAsync(user, password);
        if (!create.Succeeded)
            return AdminActionResult.Fail(Describe(create));

        await _userManager.AddToRoleAsync(user, globalRole);
        Emit(AuditCategories.User, AuditActions.UserCreated, user, new { globalRole });

        // An Admin is SiteAdmin everywhere, so a grant would be noise on the membership list.
        var implied = Authorization.EffectiveSiteRole.GlobalImplied(
            globalRole == Roles.Operator, globalRole == Roles.Viewer);
        if (!string.IsNullOrEmpty(siteTarget) && implied is not null)
        {
            var grant = siteTarget == "*"
                ? await AddMembershipAsync(user.Id, MembershipTargetType.AllSites, null, implied.Value)
                : await AddMembershipAsync(user.Id, MembershipTargetType.Site, siteTarget, implied.Value);
            if (!grant.Succeeded)
                return AdminActionResult.Fail(
                    $"Created {username}, but the site grant failed: {grant.Error} Add it under Access.");
        }

        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetEnabledAsync(string userId, bool enabled)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (!enabled && IsSelf(userId))
            return Refuse(user, AuditCategories.User, AuditActions.UserDisabled, "self",
                "You cannot disable the account you are signed in as.");

        if (!enabled && await IsLastEnabledAdminAsync(user))
            return RefuseLastAdmin(user, "disable");

        user.IsEnabled = enabled;
        await _userManager.UpdateAsync(user);
        await _userManager.UpdateSecurityStampAsync(user); // revoke live sessions on disable
        Emit(AuditCategories.User, enabled ? AuditActions.UserEnabled : AuditActions.UserDisabled, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> DeleteUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        // Deleting the account you are signed in as revokes your own session mid-action, so it is
        // refused outright rather than left to the last-admin check (which passes with two admins).
        if (IsSelf(userId))
            return Refuse(user, AuditCategories.User, AuditActions.UserDeleted, "self",
                "You cannot delete the account you are signed in as.");

        // The reserved local administrator is what the boot seed reconciles and what break-glass
        // recovery re-enables. Disabling it is enough to take it out of use.
        if (string.Equals(user.UserName, IdentityBootstrapService.AdminUserName, StringComparison.OrdinalIgnoreCase))
            return Refuse(user, AuditCategories.User, AuditActions.UserDeleted, "reserved",
                $"The built-in {IdentityBootstrapService.AdminUserName} account cannot be deleted. Disable it instead.");

        if (await IsLastEnabledAdminAsync(user))
            return RefuseLastAdmin(user, "delete");

        await RemoveAllMembershipsAsync(userId);
        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        Emit(AuditCategories.User, AuditActions.UserDeleted, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetPasswordAsync(string userId, string newPassword)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        // Clearing the temporary flag: an admin-set password is no longer the auto-generated one.
        if (user.PasswordIsTemporary)
        {
            user.PasswordIsTemporary = false;
            await _userManager.UpdateAsync(user);
        }
        Emit(AuditCategories.Auth, AuditActions.PasswordReset, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> GrantGlobalRoleAsync(string userId, string role)
    {
        if (!Roles.All.Contains(role))
            return AdminActionResult.Fail($"Unknown role '{role}'.");
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (await _userManager.IsInRoleAsync(user, role))
            return AdminActionResult.Ok();

        await _userManager.AddToRoleAsync(user, role);
        await _userManager.UpdateSecurityStampAsync(user);
        Emit(AuditCategories.Rbac, AuditActions.RoleGranted, user, new { role });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> RevokeGlobalRoleAsync(string userId, string role)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (role == Roles.Admin && IsSelf(userId))
            return Refuse(user, AuditCategories.Rbac, AuditActions.RoleRevoked, "self",
                "You cannot remove your own Admin role. Ask another administrator to change it.");

        if (role == Roles.Admin && await IsLastEnabledAdminAsync(user))
            return RefuseLastAdmin(user, "demote");

        if (!await _userManager.IsInRoleAsync(user, role))
            return AdminActionResult.Ok();

        await _userManager.RemoveFromRoleAsync(user, role);
        await _userManager.UpdateSecurityStampAsync(user);
        Emit(AuditCategories.Rbac, AuditActions.RoleRevoked, user, new { role });
        return AdminActionResult.Ok();
    }

    public async Task<IReadOnlyList<SiteMembership>> GetMembershipsAsync(string userId)
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        return await db.SiteMemberships.AsNoTracking().Where(m => m.UserId == userId).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SiteAccessGrant>> GetSiteAccessAsync(string siteSlug)
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        var grants = await db.SiteMemberships
            .AsNoTracking()
            .Where(m => m.TargetType == MembershipTargetType.Site && m.TargetId == siteSlug)
            .Select(m => new { m.Id, m.UserId, m.SiteRole })
            .ToListAsync();

        var names = await _userManager.Users
            .Where(u => grants.Select(g => g.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName);

        return grants
            .Select(g => new SiteAccessGrant(g.Id, g.UserId, names.GetValueOrDefault(g.UserId), g.SiteRole))
            .OrderBy(g => g.UserName)
            .ToList();
    }

    public async Task<AdminActionResult> AddMembershipAsync(string userId, MembershipTargetType targetType, string? targetId, SiteRole role)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (await RefuseUnlessOwnedAsync(targetType, targetId) is { } refusal)
            return refusal;

        await using (var db = await _authDbFactory.CreateDbContextAsync())
        {
            var exists = await db.SiteMemberships.AnyAsync(m =>
                m.UserId == userId && m.TargetType == targetType && m.TargetId == targetId);
            if (exists)
            {
                var existing = await db.SiteMemberships.FirstAsync(m =>
                    m.UserId == userId && m.TargetType == targetType && m.TargetId == targetId);
                existing.SiteRole = role;
            }
            else
            {
                db.SiteMemberships.Add(new SiteMembership
                {
                    UserId = userId, TargetType = targetType, TargetId = targetId, SiteRole = role,
                });
            }
            await db.SaveChangesAsync();
        }

        await BumpMembershipVersionAsync(user);
        Emit(AuditCategories.Rbac, AuditActions.MembershipChanged, user, new { targetType = targetType.ToString(), targetId, role = role.ToString() });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> RemoveMembershipAsync(string userId, int membershipId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        await using (var db = await _authDbFactory.CreateDbContextAsync())
        {
            // Resolve what the grant actually targets before deleting it: the id alone says nothing
            // about which site it belongs to, so ownership cannot be checked without reading it first.
            var target = await db.SiteMemberships
                .AsNoTracking()
                .Where(m => m.Id == membershipId && m.UserId == userId)
                .Select(m => new { m.TargetType, m.TargetId })
                .FirstOrDefaultAsync();
            if (target is null)
                return AdminActionResult.Fail("Membership not found.");

            if (await RefuseUnlessOwnedAsync(target.TargetType, target.TargetId) is { } refusal)
                return refusal;

            await db.SiteMemberships.Where(m => m.Id == membershipId && m.UserId == userId).ExecuteDeleteAsync();
        }
        await BumpMembershipVersionAsync(user);
        Emit(AuditCategories.Rbac, AuditActions.MembershipChanged, user, new { removed = membershipId });
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoleMfaPolicy>> GetRoleMfaPoliciesAsync()
    {
        var policies = new List<RoleMfaPolicy>(Roles.All.Length);
        foreach (var role in Roles.All)
        {
            var appRole = await _roleManager.FindByNameAsync(role);
            policies.Add(new RoleMfaPolicy(role, appRole?.RequireMfa == true));
        }
        return policies;
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> SetRoleRequiresMfaAsync(string role, bool requireMfa)
    {
        if (!Roles.All.Contains(role))
            return AdminActionResult.Fail($"Unknown role '{role}'.");

        var appRole = await _roleManager.FindByNameAsync(role);
        if (appRole is null) return AdminActionResult.Fail($"Role '{role}' is not provisioned.");

        appRole.RequireMfa = requireMfa;
        var result = await _roleManager.UpdateAsync(appRole);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        // Members revalidate against the new policy on their next stamp check.
        foreach (var member in await _userManager.GetUsersInRoleAsync(role))
            await _userManager.UpdateSecurityStampAsync(member);

        EmitSystemTarget(AuditCategories.Rbac, AuditActions.RoleGranted, "role", role, new { requireMfa });
        return AdminActionResult.Ok();
    }

    public async Task<IReadOnlyList<SiteGroup>> GetSiteGroupsAsync()
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        return await db.SiteGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
    }

    public async Task<AdminActionResult> CreateSiteGroupAsync(string name)
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        if (await db.SiteGroups.AnyAsync(g => g.Name == name))
            return AdminActionResult.Fail($"A group named '{name}' already exists.");
        db.SiteGroups.Add(new SiteGroup { Name = name });
        await db.SaveChangesAsync();
        EmitSystemTarget(AuditCategories.Rbac, AuditActions.MembershipChanged, "site_group", name, new { created = name });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> DeleteSiteGroupAsync(int groupId)
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        await db.SiteGroups.Where(g => g.Id == groupId).ExecuteDeleteAsync();
        EmitSystemTarget(AuditCategories.Rbac, AuditActions.MembershipChanged, "site_group", groupId.ToString(), new { deleted = groupId });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetSiteGroupMembersAsync(int groupId, IReadOnlyCollection<string> siteSlugs)
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        await db.SiteGroupMembers.Where(gm => gm.GroupId == groupId).ExecuteDeleteAsync();
        foreach (var slug in siteSlugs.Distinct(StringComparer.OrdinalIgnoreCase))
            db.SiteGroupMembers.Add(new SiteGroupMember { GroupId = groupId, SiteSlug = slug });
        await db.SaveChangesAsync();
        EmitSystemTarget(AuditCategories.Rbac, AuditActions.MembershipChanged, "site_group", groupId.ToString(), new { members = siteSlugs.Count });
        return AdminActionResult.Ok();
    }

    // --- invariants & helpers ---

    /// <summary>True if <paramref name="user"/> is an enabled Admin and no other enabled Admin remains.</summary>
    private async Task<bool> IsLastEnabledAdminAsync(ApplicationUser user)
    {
        if (!await _userManager.IsInRoleAsync(user, Roles.Admin) || !user.IsEnabled)
            return false;
        var admins = await _userManager.GetUsersInRoleAsync(Roles.Admin);
        return admins.Count(a => a.IsEnabled) <= 1;
    }

    /// <summary>
    /// Refuses a change that would lock the caller out or remove a reserved account, recording the
    /// denial as signal (design doc 05 - denials are logged, successful reads are not).
    /// </summary>
    private AdminActionResult Refuse(
        ApplicationUser user, string category, string action, string reason, string message)
    {
        _audit.Log(AuditEventBuilder.From(
            _caller.Current, category, action,
            outcome: AuditOutcomes.Denied,
            targetType: "user", targetId: user.Id, targetName: user.UserName,
            details: new { reason }));
        _logger.LogWarning("Refused {Action} on {User} ({Reason}).", action, user.UserName, reason);
        return AdminActionResult.Fail(message);
    }

    /// <summary>True when the change targets the account the caller is signed in as.</summary>
    private bool IsSelf(string userId)
        => string.Equals(userId, _caller.Current?.UserId, StringComparison.Ordinal);

    private AdminActionResult RefuseLastAdmin(ApplicationUser user, string action)
    {
        Emit(AuditCategories.Rbac, AuditActions.LastAdminRefused, user, new { action });
        _logger.LogWarning("Refused to {Action} the last enabled Admin ({User}).", action, user.UserName);
        return AdminActionResult.Fail("This is the last enabled administrator; the action was refused to avoid locking everyone out.");
    }

    private async Task BumpMembershipVersionAsync(ApplicationUser user)
    {
        user.MembershipVersion++;
        await _userManager.UpdateAsync(user);
        await _userManager.UpdateSecurityStampAsync(user);
    }

    private async Task RemoveAllMembershipsAsync(string userId)
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        await db.SiteMemberships.Where(m => m.UserId == userId).ExecuteDeleteAsync();
    }

    private void Emit(string category, string action, ApplicationUser target, object? details = null)
        => _audit.Log(AuditEventBuilder.From(
            _caller.Current, category, action,
            targetType: "user", targetId: target.Id, targetName: target.UserName, details: details));

    private void EmitSystemTarget(string category, string action, string targetType, string targetId, object? details = null)
        => _audit.Log(AuditEventBuilder.From(
            _caller.Current, category, action, targetType: targetType, targetId: targetId, details: details));

    private static string Describe(IdentityResult result)
        => string.Join("; ", result.Errors.Select(e => e.Description));
}

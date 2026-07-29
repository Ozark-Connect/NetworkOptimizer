using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Gates;

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
///
/// Gated at the service layer (design doc 06, gate 9). Account and role administration is global-Admin
/// work; the exceptions are the two site-scoped membership calls, which a Site Admin makes for the one
/// site they administer and which enforce that themselves through
/// <see cref="IdentityAdminService.RefuseUnlessOwnedAsync"/>, and the self-service password/session
/// calls, which any signed-in user makes for their OWN account and no one else's.
/// </summary>
[MutatingService]
public interface IIdentityAdminService
{
    /// <summary>
    /// Every account, for the grant pickers. The floor is Viewer rather than Admin because a Site Admin
    /// with no global role has to pick a user to grant their site to, and there is no "Admin of some
    /// site" gate to express that; the roster is names only, and every grant it feeds is separately
    /// ownership-checked.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<ApplicationUser>> ListUsersAsync();

    /// <summary>
    /// Every user with the credential and access facts the Identity tab lists: global role, whether
    /// they are enabled, MFA and passkey enrolment, and any linked external identities.
    /// </summary>
    [RequireRole(Roles.Admin)]
    Task<IReadOnlyList<UserAccountSummary>> ListUserSummariesAsync();

    /// <summary>External identities linked to one user (provider scheme plus the IdP-side subject).</summary>
    [RequireRole(Roles.Admin)]
    Task<IReadOnlyList<LinkedExternalIdentity>> GetExternalLoginsAsync(string userId);

    /// <summary>Links an external identity to a local user on an admin's behalf.</summary>
    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> LinkExternalAsync(string userId, string loginProvider, string providerKey, string? displayName);

    /// <summary>Removes a linked external identity (the user keeps any local password).</summary>
    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> UnlinkExternalAsync(string userId, string loginProvider, string providerKey);

    /// <param name="siteTarget">
    /// Site the new account is granted, at the role its global role implies: a slug, <c>"*"</c> for all
    /// sites, or null to grant nothing. Granting here rather than in a second step matters because an
    /// install with users is usually running many sites, where scoping the account IS the task.
    /// Ignored for Admin, which reaches every site regardless.
    /// </param>
    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> CreateUserAsync(
        string username, string? displayName, string? password, string globalRole, string? siteTarget = null);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> SetEnabledAsync(string userId, bool enabled);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> DeleteUserAsync(string userId);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> SetPasswordAsync(string userId, string newPassword);

    /// <summary>
    /// Backs the <b>Admin Password</b> control in Settings - Application: the signed-in global Admin
    /// sets their own password where that control has always been, and the change is what
    /// authenticates at the next sign-in.
    ///
    /// NOT the self-service path, despite also acting on the caller's own account - that is
    /// <see cref="ChangeOwnPasswordAsync"/>, which proves the current password first and stays open to
    /// any signed-in account. This one deliberately proves nothing, because the control it backs is
    /// the instance's admin credential and predates Identity. That is exactly why it is global Admin:
    /// at the Viewer floor the self-service calls carry, any signed-in caller could turn a session
    /// they found unlocked into a credential they know and can come back with. It still refuses any
    /// userId but the caller's own.
    /// </summary>
    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> SetAdminPasswordAsync(string userId, string newPassword);

    /// <summary>
    /// Changes the signed-in user's own password, proving the current one first. Distinct from
    /// <see cref="SetAdminPasswordAsync"/>, which an admin path uses without that proof: self-service
    /// has to establish that whoever is at the keyboard is the account holder and not someone who
    /// found it unlocked. Refuses any userId but the caller's own.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    [SelfServiceAction]
    Task<AdminActionResult> ChangeOwnPasswordAsync(string userId, string currentPassword, string newPassword);

    /// <summary>
    /// Clears the database-stored admin password behind the <b>Admin Password</b> control in
    /// Settings - Application, so the install falls back to APP_PASSWORD or a generated one.
    ///
    /// Gated for the same reason as <see cref="SetAdminPasswordAsync"/>: it changes what authenticates
    /// the instance, and the page wrapper around the control is not what should be deciding that. The
    /// set path went through this service while the clear path went straight to the raw settings
    /// service - two halves of one control, one of them gated and audited, the other neither.
    /// </summary>
    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> ClearAdminPasswordAsync();

    /// <summary>
    /// Rotates the signed-in user's security stamp, which every application cookie and remembered
    /// two-factor cookie is validated against - so every session for the account stops being valid.
    /// The browser that asked keeps its access only because the caller re-issues that one cookie.
    /// Refuses any userId but the caller's own.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    [SelfServiceAction]
    Task<AdminActionResult> SignOutEverywhereAsync(string userId);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> GrantGlobalRoleAsync(string userId, string role);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> RevokeGlobalRoleAsync(string userId, string role);

    /// <summary>Every grant one user holds, across sites - the instance-wide Access list.</summary>
    [RequireRole(Roles.Admin)]
    Task<IReadOnlyList<SiteMembership>> GetMembershipsAsync(string userId);

    /// <summary>
    /// Who can reach one site and at what role: the direct memberships on that slug, used by the
    /// per-site Identity tab's Access list.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    Task<IReadOnlyList<SiteAccessGrant>> GetSiteAccessAsync([SiteSlug] string siteSlug);

    /// <summary>
    /// Grants a user access. The gate is only a floor: the target may be a site, a group, or all sites,
    /// so which of those the caller is entitled to change is settled by the site-ownership check inside.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<AdminActionResult> AddMembershipAsync(string userId, MembershipTargetType targetType, string? targetId, SiteRole role);

    /// <summary>Revokes one grant. Ownership-checked against what the grant targets, as for adding.</summary>
    [RequireRole(Roles.Viewer)]
    Task<AdminActionResult> RemoveMembershipAsync(string userId, int membershipId);

    /// <summary>Per-role "require MFA" policy, in role-privilege order.</summary>
    [RequireRole(Roles.Admin)]
    Task<IReadOnlyList<RoleMfaPolicy>> GetRoleMfaPoliciesAsync();

    /// <summary>Turns the "require MFA" policy on or off for one global role.</summary>
    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> SetRoleRequiresMfaAsync(string role, bool requireMfa);

    [RequireRole(Roles.Admin)]
    Task<IReadOnlyList<SiteGroup>> GetSiteGroupsAsync();

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> CreateSiteGroupAsync(string name);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> DeleteSiteGroupAsync(int groupId);

    [RequireRole(Roles.Admin)]
    Task<AdminActionResult> SetSiteGroupMembersAsync(int groupId, IReadOnlyCollection<string> siteSlugs);
}

/// <inheritdoc />
public sealed class IdentityAdminService : IIdentityAdminService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;

    /// <summary>
    /// The scoped context <see cref="UserManager{TUser}"/> itself writes through - NOT one from the
    /// factory above, which would be a different instance with a different change tracker.
    /// </summary>
    private readonly AuthDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;
    private readonly Authorization.IEffectiveSiteRoleResolver _siteRoles;
    private readonly SiteRegistryChangeNotifier _siteRegistryChanges;
    private readonly UserSessionRevocationNotifier _revocations;
    private readonly IJwtService? _legacyJwt;
    private readonly IAdminAuthService _adminAuth;
    private readonly ILogger<IdentityAdminService> _logger;

    public IdentityAdminService(
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IDbContextFactory<AuthDbContext> authDbFactory,
        AuthDbContext db,
        IAuditLogger audit,
        ICallerContext caller,
        Authorization.IEffectiveSiteRoleResolver siteRoles,
        SiteRegistryChangeNotifier siteRegistryChanges,
        UserSessionRevocationNotifier revocations,
        ILogger<IdentityAdminService> logger,
        IAdminAuthService adminAuth,
        IJwtService? legacyJwt = null)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _authDbFactory = authDbFactory;
        _db = db;
        _audit = audit;
        _caller = caller;
        _siteRoles = siteRoles;
        _siteRegistryChanges = siteRegistryChanges;
        _revocations = revocations;
        _adminAuth = adminAuth;
        _legacyJwt = legacyJwt;
        _logger = logger;
    }

    /// <summary>
    /// Revoking the built-in admin's sessions has to reach the legacy <c>auth_token</c> too. That JWT
    /// carries no security stamp, so rotating the stamp leaves it valid, and the bridge would exchange
    /// a token captured before this revocation for a brand new cookie carrying the CURRENT stamp -
    /// which is to say a password change and a sign out everywhere would both appear to work and
    /// revoke nothing. Only the signing key can retire those tokens. The admin account is the only
    /// principal a legacy token maps to, so no other account needs this.
    ///
    /// Optional by design: a container with no <see cref="IJwtService"/> is one where legacy tokens
    /// cannot exist, so there is nothing to retire. That is what makes this removable in one piece.
    /// SUNSET: remove with <see cref="LegacyJwtBridgeMiddleware"/> one release after the cutover.
    /// </summary>
    private async Task RetireLegacyTokensIfBuiltInAdminAsync(ApplicationUser user)
    {
        if (_legacyJwt is not null
            && string.Equals(user.UserName, IdentityBootstrapService.AdminUserName, StringComparison.OrdinalIgnoreCase))
        {
            await _legacyJwt.RotateSigningKeyAsync();
        }
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

    /// <summary>
    /// The self-service invariant: a method named "own" must act on the caller's own account and
    /// nothing else. Its gate is Viewer, so without this an Operator could hand it an Admin's id and
    /// reset that password. System and no-auth callers are unscoped, as everywhere else.
    /// Returns null when the change is allowed.
    /// </summary>
    private AdminActionResult? RefuseUnlessSelf(string userId)
    {
        var caller = _caller.Current;
        if (caller is null || caller.IsSystem || caller.AuthenticationDisabled)
            return null;

        return string.Equals(caller.UserId, userId, StringComparison.Ordinal)
            ? null
            : AdminActionResult.Fail("You can only change your own account.");
    }

    /// <summary>
    /// Current display name for each linked-identity key. ASP.NET Identity stores the provider's name
    /// ON the login row, written once when the identity was linked - it is a copy, not a reference, so
    /// renaming a provider never reaches it and the old name is shown forever. The provider table is
    /// the one source of truth for what a provider is called, so resolve against it and keep the
    /// stored copy only as the fallback for a provider that has since been deleted, where it is the
    /// last record of what that identity was.
    /// </summary>
    private async Task<Dictionary<string, string>> ProviderNamesAsync()
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        var providers = await db.FederationProviders
            .AsNoTracking()
            .Select(p => new { p.Type, p.Scheme, p.DisplayName })
            .ToListAsync();

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in providers)
            names[FederationSchemeKey.For(p.Type, p.Scheme)] = p.DisplayName;
        return names;
    }

    /// <summary>
    /// Loads a user with values fresh from the database, for every path that is about to change one.
    ///
    /// The identity context is scoped, and in Blazor Server a scope is the whole circuit - so the user
    /// list this card loaded when the tab was opened is still tracked an hour later. A query returns
    /// the instance already being tracked rather than overwriting its values, so its concurrency stamp
    /// stays as it was at page load. If the account has signed in since (which stamps LastLoginAt and
    /// rotates the stamp), the write silently fails its concurrency check - and because these callers
    /// ignored the result, the UI reported the change had been made while nothing had. Disabling an
    /// account that way left it able to go on signing in.
    /// </summary>
    private async Task<ApplicationUser?> LoadForUpdateAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is not null)
            await _db.Entry(user).ReloadAsync();
        return user;
    }

    public async Task<IReadOnlyList<ApplicationUser>> ListUsersAsync()
        => await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();

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

        var providerNames = await ProviderNamesAsync();

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
                logins.Select(l => providerNames.TryGetValue(l.LoginProvider, out var name)
                    ? name
                    : l.ProviderDisplayName ?? l.LoginProvider).ToList(),
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
        var names = await ProviderNamesAsync();
        return logins
            .Select(l => new LinkedExternalIdentity(
                l.LoginProvider,
                l.ProviderKey,
                names.TryGetValue(l.LoginProvider, out var current) ? current : l.ProviderDisplayName))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> LinkExternalAsync(string userId, string loginProvider, string providerKey, string? displayName)
    {
        var user = await LoadForUpdateAsync(userId);
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
        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        var result = await _userManager.RemoveLoginAsync(user, loginProvider, providerKey);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        await _userManager.UpdateSecurityStampAsync(user);
        await RetireLegacyTokensIfBuiltInAdminAsync(user);
        Emit(AuditCategories.User, AuditActions.ExternalUnlinked, user, new { loginProvider });
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> ChangeOwnPasswordAsync(string userId, string currentPassword, string newPassword)
    {
        if (RefuseUnlessSelf(userId) is { } refusal) return refusal;

        var user = await LoadForUpdateAsync(userId);
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

        await ClearTemporaryPasswordFlagAsync(user);
        await SyncAdminSettingsIfBuiltInAdminAsync(user, newPassword);
        await RetireLegacyTokensIfBuiltInAdminAsync(user);
        Emit(AuditCategories.User, AuditActions.PasswordReset, user, new { self = true });
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> SignOutEverywhereAsync(string userId)
    {
        if (RefuseUnlessSelf(userId) is { } refusal) return refusal;

        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        var stamped = await _userManager.UpdateSecurityStampAsync(user);
        if (!stamped.Succeeded)
            return AdminActionResult.Fail(Describe(stamped));
        await RetireLegacyTokensIfBuiltInAdminAsync(user);
        Emit(AuditCategories.Auth, AuditActions.SignedOutEverywhere, user, new { self = true });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetAdminPasswordAsync(string userId, string newPassword)
    {
        if (RefuseUnlessSelf(userId) is { } refusal) return refusal;

        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        // A federated-only account (or one carried over from an install that never had a local
        // password) has no hash to reset, so the change becomes an add.
        var result = await _userManager.HasPasswordAsync(user)
            ? await _userManager.ResetPasswordAsync(user, await _userManager.GeneratePasswordResetTokenAsync(user), newPassword)
            : await _userManager.AddPasswordAsync(user, newPassword);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        await ClearTemporaryPasswordFlagAsync(user);
        await SyncAdminSettingsIfBuiltInAdminAsync(user, newPassword);
        await RetireLegacyTokensIfBuiltInAdminAsync(user);
        Emit(AuditCategories.Auth, AuditActions.PasswordChanged, user);
        return AdminActionResult.Ok();
    }

    /// <inheritdoc />
    public async Task<AdminActionResult> ClearAdminPasswordAsync()
    {
        await _adminAuth.ClearDatabasePasswordAsync();
        EmitSystemTarget(AuditCategories.Auth, AuditActions.PasswordChanged, "admin_password", "instance");
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

        // Reported rather than dropped: an account that exists with no role is not the account the
        // admin asked for, and it is worse than none - it can sign in and is refused everywhere.
        var roleGranted = await _userManager.AddToRoleAsync(user, globalRole);
        if (!roleGranted.Succeeded)
            return AdminActionResult.Fail(
                $"Created {username}, but the {globalRole} role could not be granted: {Describe(roleGranted)}");

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
        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (!enabled && IsSelf(userId))
            return Refuse(user, AuditCategories.User, AuditActions.UserDisabled, "self",
                "You cannot disable the account you are signed in as.");

        if (!enabled && await IsLastEnabledAdminAsync(user))
            return RefuseLastAdmin(user, "disable");

        user.IsEnabled = enabled;
        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
            return AdminActionResult.Fail(Describe(update));
        await _userManager.UpdateSecurityStampAsync(user); // revoke live sessions on disable
        if (!enabled)
            _revocations.NotifyRevoked(user.Id);
        Emit(AuditCategories.User, enabled ? AuditActions.UserEnabled : AuditActions.UserDisabled, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> DeleteUserAsync(string userId)
    {
        var user = await LoadForUpdateAsync(userId);
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

        // The account is gone, so there is no stamp left to fail a revalidation against - the circuit
        // would keep rendering until it happened to ask. Tell it now.
        _revocations.NotifyRevoked(user.Id);
        Emit(AuditCategories.User, AuditActions.UserDeleted, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetPasswordAsync(string userId, string newPassword)
    {
        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, newPassword);
        if (!result.Succeeded) return AdminActionResult.Fail(Describe(result));

        // Clearing the temporary flag: an admin-set password is no longer the auto-generated one.
        await ClearTemporaryPasswordFlagAsync(user);
        await RetireLegacyTokensIfBuiltInAdminAsync(user);
        Emit(AuditCategories.Auth, AuditActions.PasswordReset, user);
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> GrantGlobalRoleAsync(string userId, string role)
    {
        if (!Roles.All.Contains(role))
            return AdminActionResult.Fail($"Unknown role '{role}'.");
        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (await _userManager.IsInRoleAsync(user, role))
            return AdminActionResult.Ok();

        var granted = await _userManager.AddToRoleAsync(user, role);
        if (!granted.Succeeded)
            return AdminActionResult.Fail(Describe(granted));

        var changed = await PermissionsChangedAsync(user);
        if (!changed.Succeeded)
            return AdminActionResult.Fail(Describe(changed));

        Emit(AuditCategories.Rbac, AuditActions.RoleGranted, user, new { role });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> RevokeGlobalRoleAsync(string userId, string role)
    {
        var user = await LoadForUpdateAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

        if (role == Roles.Admin && IsSelf(userId))
            return Refuse(user, AuditCategories.Rbac, AuditActions.RoleRevoked, "self",
                "You cannot remove your own Admin role. Ask another administrator to change it.");

        if (role == Roles.Admin && await IsLastEnabledAdminAsync(user))
            return RefuseLastAdmin(user, "demote");

        if (!await _userManager.IsInRoleAsync(user, role))
            return AdminActionResult.Ok();

        // Checked, because the failure this drops is the dangerous direction: the row is written
        // under a concurrency stamp, so a user who signed in since the tab was opened fails the save
        // and keeps the role - while the audit log records RoleRevoked and the UI says it worked.
        var revoked = await _userManager.RemoveFromRoleAsync(user, role);
        if (!revoked.Succeeded)
            return AdminActionResult.Fail(Describe(revoked));

        var changed = await PermissionsChangedAsync(user);
        if (!changed.Succeeded)
            return AdminActionResult.Fail(Describe(changed));

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
        var user = await LoadForUpdateAsync(userId);
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
                    UserId = userId,
                    TargetType = targetType,
                    TargetId = targetId,
                    SiteRole = role,
                });
            }
            await db.SaveChangesAsync();
        }

        var granted = await PermissionsChangedAsync(user);
        if (!granted.Succeeded)
            return AdminActionResult.Fail(Describe(granted));

        Emit(AuditCategories.Rbac, AuditActions.MembershipChanged, user, new { targetType = targetType.ToString(), targetId, role = role.ToString() });
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> RemoveMembershipAsync(string userId, int membershipId)
    {
        var user = await LoadForUpdateAsync(userId);
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
        var removed = await PermissionsChangedAsync(user);
        if (!removed.Succeeded)
            return AdminActionResult.Fail(Describe(removed));

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
        {
            await _userManager.UpdateSecurityStampAsync(member);
            // A bridged legacy session is precisely the one that never met the second-factor
            // requirement, so turning the policy on has to reach it as well as the cookies.
            await RetireLegacyTokensIfBuiltInAdminAsync(member);
        }

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
        GroupAccessChanged();
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
        GroupAccessChanged();
        EmitSystemTarget(AuditCategories.Rbac, AuditActions.MembershipChanged, "site_group", groupId.ToString(), new { members = siteSlugs.Count });
        return AdminActionResult.Ok();
    }

    /// <summary>
    /// Drops every cached role resolution after a site group changes.
    ///
    /// Unlike a direct membership, a group change moves access for everyone holding a grant that
    /// points at the group, and the grant rows themselves do not move - so there is no per-user edit
    /// to hang an invalidation off, and no way to enumerate the affected users without walking every
    /// grant. Without this the cache went on answering from the old group for its full ten minutes,
    /// which for a REVOCATION means access is retained rather than merely delayed: taking a site out
    /// of a group, or deleting the group outright, left everyone in it still reaching that site.
    /// </summary>
    private void GroupAccessChanged()
    {
        _siteRoles.InvalidateAll();
        _siteRegistryChanges.NotifySitesChanged();
    }

    // --- invariants & helpers ---

    /// <summary>
    /// True if <paramref name="user"/> is an enabled Admin and no other enabled Admin remains.
    ///
    /// The count comes from a fresh no-tracking context, NOT from
    /// <see cref="UserManager{TUser}.GetUsersInRoleAsync"/>. That runs on the scoped context a Blazor
    /// circuit keeps for its whole life, so EF identity resolution hands back the instances already
    /// tracked from when the Identity tab was opened - with the IsEnabled values they had then. Two
    /// admins disabled in different circuits would each see the other as still enabled and both
    /// writes would pass the check, leaving zero. The write itself still goes through
    /// <see cref="LoadForUpdateAsync"/>; this is the same staleness one level out, in the cohort
    /// rather than the target.
    /// </summary>
    private async Task<bool> IsLastEnabledAdminAsync(ApplicationUser user)
    {
        if (!await _userManager.IsInRoleAsync(user, Roles.Admin) || !user.IsEnabled)
            return false;

        await using var db = await _authDbFactory.CreateDbContextAsync();
        var enabledAdmins = await db.Users
            .AsNoTracking()
            .Where(u => u.IsEnabled && db.UserRoles
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
                .Any(x => x.UserId == u.Id && x.Name == Roles.Admin))
            .CountAsync();

        return enabledAdmins <= 1;
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

    /// <summary>
    /// Clears the "this is still the auto-generated password" nag after a real one is set. Logged
    /// rather than returned: the password change itself has already succeeded by this point, so
    /// failing the call would report the opposite of what happened. The cost of losing it is that the
    /// banner keeps asking for a password the user has already set.
    /// </summary>
    /// <summary>
    /// The install's own record of where the admin password comes from lives in AdminSettings, and it
    /// is what Settings - Application reports and what the "set a real password" nag reads. A password
    /// set through Settings goes through SaveAdminSettingsAsync and flips that row to Database; a
    /// password set here did not touch it at all, so the install went on describing itself as running
    /// the startup-log password long after the operator had replaced it.
    ///
    /// Only for the built-in admin: it is the account that row is about.
    /// </summary>
    private async Task SyncAdminSettingsIfBuiltInAdminAsync(ApplicationUser user, string newPassword)
    {
        if (!string.Equals(user.UserName, IdentityBootstrapService.AdminUserName, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await _adminAuth.SaveAdminSettingsAsync(newPassword, enabled: true);
        }
        catch (Exception ex)
        {
            // The Identity password IS changed by this point, so this must not fail the operation -
            // the worst outcome is the source display staying stale, which is what it did before.
            _logger.LogWarning(ex, "Admin password changed but the stored admin settings could not be updated.");
        }
    }

    private async Task ClearTemporaryPasswordFlagAsync(ApplicationUser user)
    {
        if (!user.PasswordIsTemporary)
            return;

        user.PasswordIsTemporary = false;
        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            _logger.LogWarning(
                "Password was changed for {User} but the temporary-password flag could not be cleared: {Errors}",
                user.UserName, Describe(update));
        }
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

    /// <summary>
    /// Records that a user's permissions changed - a global role, or a site membership.
    ///
    /// The security stamp is deliberately NOT rotated here. Rotating it ends every session the account
    /// has, which is right for a revocation (disable, password change, sign out everywhere) and wrong
    /// for this: being given access to one more site is not grounds for throwing someone out of the
    /// app. The version advance is the signal instead, and a live circuit answers it by re-issuing its
    /// own cookie (see RevalidatingIdentityAuthenticationStateProvider), which is how the new roles
    /// reach a session that is already running.
    ///
    /// Site access is read from the database rather than the cookie, so dropping the cached resolution
    /// is all it takes for a membership change to apply to the user's very next action.
    /// </summary>
    private async Task<IdentityResult> PermissionsChangedAsync(ApplicationUser user)
    {
        user.MembershipVersion++;
        var update = await _userManager.UpdateAsync(user);
        if (!update.Succeeded)
        {
            // The version bump IS the signal. Losing it means live circuits never notice the change
            // and go on running with the old permissions, so the caller has to hear about it rather
            // than be told the grant landed.
            _logger.LogWarning(
                "Membership version bump failed for {User}: {Errors}", user.UserName, Describe(update));
            return update;
        }

        _siteRoles.Invalidate(user.Id);

        // Being granted or denied a site changes the site list this instant, and waiting for the
        // session refresh to come round would leave the switcher offering yesterday's sites. Every
        // circuit rebuilds its own filtered list, so the broadcast tells the right person without
        // needing to know who they are.
        _siteRegistryChanges.NotifySitesChanged();

        // The list of sites is not the same question as what this user may do on them. Roles are read
        // from the principal, so a demotion that only broadcast the site change sat there looking
        // applied while the user kept every button their old role had - until revalidation came round
        // five minutes later. Tell their circuits to pick up a current principal now.
        _revocations.NotifyPermissionsChanged(user.Id);
        return IdentityResult.Success;
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

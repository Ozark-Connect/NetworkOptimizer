using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

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
    Task<ApplicationUser?> FindByIdAsync(string userId);
    Task<IReadOnlyList<string>> GetGlobalRolesAsync(ApplicationUser user);

    Task<AdminActionResult> CreateUserAsync(string username, string? displayName, string? password, string globalRole);
    Task<AdminActionResult> SetEnabledAsync(string userId, bool enabled);
    Task<AdminActionResult> DeleteUserAsync(string userId);
    Task<AdminActionResult> SetPasswordAsync(string userId, string newPassword);

    Task<AdminActionResult> GrantGlobalRoleAsync(string userId, string role);
    Task<AdminActionResult> RevokeGlobalRoleAsync(string userId, string role);

    Task<IReadOnlyList<SiteMembership>> GetMembershipsAsync(string userId);
    Task<AdminActionResult> AddMembershipAsync(string userId, MembershipTargetType targetType, string? targetId, SiteRole role);
    Task<AdminActionResult> RemoveMembershipAsync(string userId, int membershipId);

    Task<IReadOnlyList<SiteGroup>> GetSiteGroupsAsync();
    Task<AdminActionResult> CreateSiteGroupAsync(string name);
    Task<AdminActionResult> DeleteSiteGroupAsync(int groupId);
    Task<AdminActionResult> SetSiteGroupMembersAsync(int groupId, IReadOnlyCollection<string> siteSlugs);
}

/// <inheritdoc />
public sealed class IdentityAdminService : IIdentityAdminService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IAuditLogger _audit;
    private readonly ICallerContext _caller;
    private readonly ILogger<IdentityAdminService> _logger;

    public IdentityAdminService(
        UserManager<ApplicationUser> userManager,
        IDbContextFactory<AuthDbContext> authDbFactory,
        IAuditLogger audit,
        ICallerContext caller,
        ILogger<IdentityAdminService> logger)
    {
        _userManager = userManager;
        _authDbFactory = authDbFactory;
        _audit = audit;
        _caller = caller;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ApplicationUser>> ListUsersAsync()
        => await _userManager.Users.OrderBy(u => u.UserName).ToListAsync();

    public Task<ApplicationUser?> FindByIdAsync(string userId) => _userManager.FindByIdAsync(userId)!;

    public async Task<IReadOnlyList<string>> GetGlobalRolesAsync(ApplicationUser user)
        => (await _userManager.GetRolesAsync(user)).ToList();

    public async Task<AdminActionResult> CreateUserAsync(string username, string? displayName, string? password, string globalRole)
    {
        if (!GlobalRoles.All.Contains(globalRole))
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
        return AdminActionResult.Ok();
    }

    public async Task<AdminActionResult> SetEnabledAsync(string userId, bool enabled)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

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
        if (!GlobalRoles.All.Contains(role))
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

        if (role == GlobalRoles.Admin && await IsLastEnabledAdminAsync(user))
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

    public async Task<AdminActionResult> AddMembershipAsync(string userId, MembershipTargetType targetType, string? targetId, SiteRole role)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null) return AdminActionResult.Fail("User not found.");

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
            await db.SiteMemberships.Where(m => m.Id == membershipId && m.UserId == userId).ExecuteDeleteAsync();
        }
        await BumpMembershipVersionAsync(user);
        Emit(AuditCategories.Rbac, AuditActions.MembershipChanged, user, new { removed = membershipId });
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
        if (!await _userManager.IsInRoleAsync(user, GlobalRoles.Admin) || !user.IsEnabled)
            return false;
        var admins = await _userManager.GetUsersInRoleAsync(GlobalRoles.Admin);
        return admins.Count(a => a.IsEnabled) <= 1;
    }

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

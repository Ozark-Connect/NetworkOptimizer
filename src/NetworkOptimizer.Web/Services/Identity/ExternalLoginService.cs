using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>Outcome of processing an external (federated) authentication.</summary>
public enum ExternalLoginOutcome
{
    /// <summary>Linked to a local user and signed in.</summary>
    SignedIn,

    /// <summary>No local link and JIT is off - the user needs an admin to create/link an account.</summary>
    NoAccount,

    /// <summary>The linked local account is disabled.</summary>
    Disabled,
}

/// <summary>
/// The single external-callback handler for every federation provider (design doc 03/06, gate 6):
/// resolves an external principal to a local user via <c>UserLogins</c>, optionally JIT-provisions,
/// applies role/site mappings per the provider's mode, and signs in. There is NO email-based
/// auto-linking - linking an external identity to an existing local user is always an explicit act.
/// </summary>
public interface IExternalLoginService
{
    Task<ExternalLoginOutcome> ProcessAsync(FederationProvider provider, ClaimsPrincipal external);
}

/// <inheritdoc />
public sealed class ExternalLoginService : IExternalLoginService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IAuditLogger _audit;

    public ExternalLoginService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IDbContextFactory<AuthDbContext> authDbFactory,
        IAuditLogger audit)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _authDbFactory = authDbFactory;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<ExternalLoginOutcome> ProcessAsync(FederationProvider provider, ClaimsPrincipal external)
    {
        var subject = ClaimValue(external, provider.SubjectClaim, ClaimTypes.NameIdentifier, "sub");
        if (string.IsNullOrEmpty(subject))
        {
            EmitRejected(provider, reason: "no subject claim");
            return ExternalLoginOutcome.NoAccount;
        }

        var loginProvider = SchemeKey(provider);
        var existing = await _userManager.FindByLoginAsync(loginProvider, subject);

        if (existing is not null)
        {
            if (!existing.IsEnabled)
            {
                EmitRejected(provider, reason: "account disabled", user: existing, subject: subject);
                return ExternalLoginOutcome.Disabled;
            }

            if (provider.RoleMappingMode == RoleMappingMode.IdpAuthoritative)
                await ResyncMappingsAsync(provider, existing, external);

            await SignInAsync(existing, provider);
            return ExternalLoginOutcome.SignedIn;
        }

        // No link. JIT-provision or reject - never auto-link by email.
        if (provider.JitProvisioning != JitProvisioningMode.CreateOnFirstLogin)
        {
            EmitRejected(provider, reason: "no linked account and JIT is off", subject: subject);
            return ExternalLoginOutcome.NoAccount;
        }

        var created = await JitProvisionAsync(provider, external, subject, loginProvider);
        if (created is null)
            return ExternalLoginOutcome.NoAccount;

        await SignInAsync(created, provider);
        return ExternalLoginOutcome.SignedIn;
    }

    private async Task<ApplicationUser?> JitProvisionAsync(
        FederationProvider provider, ClaimsPrincipal external, string subject, string loginProvider)
    {
        var username = await UniqueUsernameAsync(
            ClaimValue(external, provider.UsernameClaim, ClaimTypes.Name, "preferred_username") ?? subject);

        var user = new ApplicationUser
        {
            UserName = username,
            DisplayName = ClaimValue(external, provider.DisplayNameClaim, ClaimTypes.GivenName, "name"),
            Email = ClaimValue(external, provider.EmailClaim, ClaimTypes.Email, "email"),
            IsEnabled = true,
            LastLoginMethod = SchemeKey(provider),
        };

        var create = await _userManager.CreateAsync(user);
        if (!create.Succeeded)
        {
            // Silently returning null here made a failed create indistinguishable from "no account",
            // with nothing recorded to say a create had even been attempted.
            EmitRejected(
                provider,
                reason: "JIT provisioning failed: "
                    + string.Join("; ", create.Errors.Select(e => e.Description)),
                subject: subject);
            return null;
        }

        await _userManager.AddLoginAsync(user, new UserLoginInfo(loginProvider, subject, provider.DisplayName));
        await ApplyMappingsAsync(provider, user, external, isInitial: true);

        _audit.Log(AuditEventBuilder.From(
            CallerInfo.System($"federation:{loginProvider}"),
            AuditCategories.User, AuditActions.UserJitCreated, AuditOutcomes.Success,
            targetType: "user", targetId: user.Id, targetName: user.UserName,
            details: new { provider = provider.DisplayName }));
        return user;
    }

    /// <summary>Applies global-role and site-membership mappings from the provider's config.</summary>
    private async Task ApplyMappingsAsync(FederationProvider provider, ApplicationUser user, ClaimsPrincipal external, bool isInitial)
    {
        var groups = GroupValues(external, provider.GroupsClaim);

        // Global roles.
        var mappedRoles = provider.RoleMappings
            .Where(m => groups.Contains(m.GroupOrClaimValue))
            .Select(m => m.GlobalRole)
            .Where(r => Roles.All.Contains(r))
            .Distinct()
            .ToList();

        foreach (var role in mappedRoles)
        {
            if (!await _userManager.IsInRoleAsync(user, role))
                await _userManager.AddToRoleAsync(user, role);
        }

        // Site memberships.
        await using var db = await _authDbFactory.CreateDbContextAsync();
        var mappedSites = provider.SiteMappings.Where(m => groups.Contains(m.GroupOrClaimValue)).ToList();
        foreach (var m in mappedSites)
        {
            var exists = await db.SiteMemberships.AnyAsync(x =>
                x.UserId == user.Id && x.TargetType == m.TargetType && x.TargetId == m.TargetValue);
            if (!exists)
            {
                db.SiteMemberships.Add(new SiteMembership
                {
                    UserId = user.Id, TargetType = m.TargetType, TargetId = m.TargetValue, SiteRole = m.SiteRole,
                });
            }
        }
        if (mappedSites.Count > 0)
            await db.SaveChangesAsync();
    }

    /// <summary>
    /// IdP-authoritative resync at every login: recompute roles from claims. The last-admin invariant
    /// is never violated - a demotion that would remove the final Admin is skipped and audited.
    /// </summary>
    private async Task ResyncMappingsAsync(FederationProvider provider, ApplicationUser user, ClaimsPrincipal external)
    {
        var groups = GroupValues(external, provider.GroupsClaim);
        var desiredRoles = provider.RoleMappings
            .Where(m => groups.Contains(m.GroupOrClaimValue))
            .Select(m => m.GlobalRole)
            .Where(r => Roles.All.Contains(r))
            .Distinct()
            .ToHashSet();

        var currentRoles = (await _userManager.GetRolesAsync(user)).ToHashSet();

        foreach (var add in desiredRoles.Except(currentRoles))
            await _userManager.AddToRoleAsync(user, add);

        foreach (var remove in currentRoles.Except(desiredRoles))
        {
            if (remove == Roles.Admin && await IsLastEnabledAdminAsync(user))
            {
                _audit.Log(AuditEventBuilder.From(
                    CallerInfo.System($"federation:{SchemeKey(provider)}"),
                    AuditCategories.Federation, AuditActions.IdpResyncConflict, AuditOutcomes.Denied,
                    targetType: "user", targetId: user.Id, targetName: user.UserName,
                    details: new { skipped = "last-admin demotion" }));
                continue;
            }
            await _userManager.RemoveFromRoleAsync(user, remove);
        }

        await ApplyMappingsAsync(provider, user, external, isInitial: false);
        await _userManager.UpdateSecurityStampAsync(user);
    }

    private async Task<bool> IsLastEnabledAdminAsync(ApplicationUser user)
    {
        var admins = await _userManager.GetUsersInRoleAsync(Roles.Admin);
        return admins.Count(a => a.IsEnabled) <= 1;
    }

    private async Task SignInAsync(ApplicationUser user, FederationProvider provider)
    {
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginMethod = SchemeKey(provider);
        await _userManager.UpdateAsync(user);
        await _signInManager.SignInAsync(user, isPersistent: false, SchemeKey(provider));

        _audit.Log(AuditEventBuilder.From(
            CallerInfo.System($"federation:{SchemeKey(provider)}") with { UserId = user.Id, ActorName = user.UserName ?? "" },
            AuditCategories.Auth, AuditActions.LoginSuccess, AuditOutcomes.Success,
            targetType: "user", targetId: user.Id, targetName: user.UserName));
    }

    private async Task<string> UniqueUsernameAsync(string candidate)
    {
        var baseName = candidate.Trim();
        if (await _userManager.FindByNameAsync(baseName) is null)
            return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var suffixed = $"{baseName}{i}";
            if (await _userManager.FindByNameAsync(suffixed) is null)
                return suffixed;
        }
        return $"{baseName}-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Records a refused federated sign-in. The subject is included because without it "no account" is
    /// unanswerable: the operator cannot tell whether the provider sent something unexpected or whether
    /// the link they created does not match it - which is exactly the question a rejected login raises.
    /// It is an opaque provider-assigned identifier, not a credential, and the login it belongs to has
    /// already been refused. loginProvider is recorded alongside it because the link is stored and
    /// looked up under that exact key, and a mismatch there looks identical to a missing account.
    /// </summary>
    private void EmitRejected(
        FederationProvider provider, string reason, ApplicationUser? user = null, string? subject = null)
        => _audit.Log(AuditEventBuilder.From(
            CallerInfo.System($"federation:{SchemeKey(provider)}"),
            AuditCategories.Auth, AuditActions.FederatedLoginRejected, AuditOutcomes.Denied,
            targetType: user is null ? "provider" : "user", targetId: user?.Id ?? provider.Scheme,
            targetName: user?.UserName ?? provider.DisplayName,
            details: new { reason, subject, loginProvider = SchemeKey(provider) }));

    private static string SchemeKey(FederationProvider provider) => FederationSchemeKey.For(provider);

    private static string? ClaimValue(ClaimsPrincipal principal, string? configured, params string[] fallbacks)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            var v = principal.FindFirst(configured)?.Value;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        foreach (var f in fallbacks)
        {
            var v = principal.FindFirst(f)?.Value;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    private static HashSet<string> GroupValues(ClaimsPrincipal principal, string? groupsClaim)
    {
        var claimType = string.IsNullOrEmpty(groupsClaim) ? "groups" : groupsClaim;
        return principal.FindAll(claimType).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

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

    /// <summary>Their role requires a second factor and they have none enrolled.</summary>
    RequiresMfaEnrollment,

    /// <summary>
    /// Their role requires a second factor and they have a passkey, which proves one when it is the
    /// credential actually used - so send them to use it.
    /// </summary>
    RequiresPasskeySignIn,

    /// <summary>
    /// Their role requires a second factor and an authenticator app is enrolled. Identity's pending
    /// two-factor state has been established, so the caller sends them to the code entry page.
    /// </summary>
    RequiresTwoFactor,

    /// <summary>
    /// Their role requires a second factor, none of the enrolled ones can be challenged here, and the
    /// two-factor state could not be established. They come in locally instead.
    /// </summary>
    RequiresLocalMfa,
}

/// <summary>
/// The single external-callback handler for every federation provider (design doc 03/06, gate 6):
/// resolves an external principal to a local user via <c>UserLogins</c>, optionally JIT-provisions,
/// applies role/site mappings per the provider's mode, and signs in. There is NO email-based
/// auto-linking - linking an external identity to an existing local user is always an explicit act.
/// </summary>
public interface IExternalLoginService
{
    /// <param name="rememberMe">
    /// The Keep me signed in choice made on the login page, carried across the round trip to the IdP
    /// (in the external cookie's properties for OIDC, in RelayState for SAML). Without it every
    /// federated sign-in issued a session-scoped cookie whatever the user asked for.
    /// </param>
    Task<ExternalLoginOutcome> ProcessAsync(
        FederationProvider provider, ClaimsPrincipal external, bool rememberMe);
}

/// <inheritdoc />
public sealed class ExternalLoginService : IExternalLoginService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly IAuditLogger _audit;
    private readonly IMfaService _mfa;

    public ExternalLoginService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IDbContextFactory<AuthDbContext> authDbFactory,
        IAuditLogger audit,
        IMfaService mfa)
    {
        _mfa = mfa;
        _userManager = userManager;
        _signInManager = signInManager;
        _authDbFactory = authDbFactory;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<ExternalLoginOutcome> ProcessAsync(
        FederationProvider provider, ClaimsPrincipal external, bool rememberMe)
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

            // Worked out before the second-factor check and applied only after it passes. The check
            // has to see the roles this login WILL hold - a resync that raises someone into a role
            // demanding MFA must demand it on this very login, not the next one - while a refusal has
            // to leave the store as it found it. Resyncing first did the opposite on both counts: the
            // check read the pre-resync roles, and a refused login kept the role it had just written.
            var resyncRoles = provider.RoleMappingMode == RoleMappingMode.IdpAuthoritative
                ? ComputeResyncRoles(provider, external)
                : null;

            // The union, deliberately: a role being removed still counts until it is actually gone
            // (and the last-admin rule can keep it), so the stricter of the two answers wins.
            var effectiveRoles = (await _userManager.GetRolesAsync(existing)).ToHashSet();
            if (resyncRoles is not null)
                effectiveRoles.UnionWith(resyncRoles);

            var unmet = await SecondFactorUnmetAsync(provider, existing, external, subject, rememberMe, effectiveRoles);
            if (unmet is not null)
                return unmet.Value;

            if (resyncRoles is not null)
                await ApplyResyncAsync(provider, existing, external, resyncRoles);

            await SignInAsync(existing, provider, rememberMe);
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

        // Applies to a just-created account too: JIT can land someone straight into a role that
        // requires a second factor, and skipping the check here would make provisioning the way round
        // it. Its mappings are already written, so the stored roles ARE the effective ones - and an
        // account that cannot sign in yet grants nothing by existing.
        var unmetForNew = await SecondFactorUnmetAsync(
            provider, created, external, subject, rememberMe, await _userManager.GetRolesAsync(created));
        if (unmetForNew is not null)
            return unmetForNew.Value;

        await SignInAsync(created, provider, rememberMe);
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
    /// The roles an IdP-authoritative resync would land the user on, computed from the claims alone
    /// and writing nothing. Split out from <see cref="ApplyResyncAsync"/> so the second-factor check
    /// can run against the roles the login is ABOUT to have and still leave the store untouched if it
    /// refuses - resyncing first meant a refused login kept the role it had just been granted.
    /// </summary>
    private HashSet<string> ComputeResyncRoles(FederationProvider provider, ClaimsPrincipal external)
    {
        var groups = GroupValues(external, provider.GroupsClaim);
        return provider.RoleMappings
            .Where(m => groups.Contains(m.GroupOrClaimValue))
            .Select(m => m.GlobalRole)
            .Where(r => Roles.All.Contains(r))
            .Distinct()
            .ToHashSet();
    }

    /// <summary>
    /// IdP-authoritative resync at every login: apply the roles <see cref="ComputeResyncRoles"/>
    /// worked out. The last-admin invariant is never violated - a demotion that would remove the
    /// final Admin is skipped and audited.
    /// </summary>
    private async Task ApplyResyncAsync(
        FederationProvider provider, ApplicationUser user, ClaimsPrincipal external, HashSet<string> desiredRoles)
    {
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


    /// <summary>
    /// The role-MFA policy for a federated sign-in - the same requirement local sign-in enforces, which
    /// this path skipped entirely: a role demanding a second factor was satisfied by simply arriving
    /// through a provider. Returns null when the sign-in may proceed.
    ///
    /// TrustIdpMfa is what decides whether the provider's own second factor counts. It is the setting's
    /// first use; until now it was stored, shown in the UI, and read by nothing.
    /// </summary>
    private async Task<ExternalLoginOutcome?> SecondFactorUnmetAsync(
        FederationProvider provider, ApplicationUser user, ClaimsPrincipal external, string subject,
        bool rememberMe, IEnumerable<string> effectiveRoles)
    {
        if (!await _mfa.AnyRoleRequiresMfaAsync(effectiveRoles))
            return null;

        if (provider.TrustIdpMfa && IdpAssertedSecondFactor(external))
            return null;

        if (!await _mfa.HasSecondFactorAsync(user))
        {
            EmitRejected(provider, reason: "role requires a second factor and none is enrolled",
                user: user, subject: subject);
            return ExternalLoginOutcome.RequiresMfaEnrollment;
        }

        // An authenticator app CAN be challenged here: signing in through Identity's external path
        // rather than signing the principal in directly leaves the pending two-factor state behind, and
        // the existing /login/2fa page picks it up. Preferred over the passkey route when both are
        // enrolled, because it completes the sign-in the user actually started.
        if (await _mfa.IsEnabledAsync(user))
        {
            var result = await _signInManager.ExternalLoginSignInAsync(
                SchemeKey(provider), subject, isPersistent: rememberMe, bypassTwoFactor: false);

            if (result.RequiresTwoFactor)
            {
                EmitRejected(provider, reason: "role requires a second factor; sent to authenticator entry",
                    user: user, subject: subject);
                return ExternalLoginOutcome.RequiresTwoFactor;
            }

            // Two-factor state could not be established, so there is nothing to complete. Refuse rather
            // than fall through to a session that never proved a second factor.
            EmitRejected(provider,
                reason: $"role requires a second factor; external two-factor sign-in did not challenge ({result})",
                user: user, subject: subject);
            return ExternalLoginOutcome.RequiresLocalMfa;
        }

        // A passkey satisfies the requirement only when it is the credential actually used, and here it
        // was not - the provider authenticated them.
        var hasPasskey = (await _userManager.GetPasskeysAsync(user)).Count > 0;
        EmitRejected(
            provider,
            reason: hasPasskey
                ? "role requires a second factor; the provider did not assert one and the passkey was not used"
                : "role requires a second factor; nothing enrolled can be challenged here",
            user: user,
            subject: subject);
        return hasPasskey
            ? ExternalLoginOutcome.RequiresPasskeySignIn
            : ExternalLoginOutcome.RequiresLocalMfa;
    }

    /// <summary>
    /// Whether the provider says it performed a second factor. OIDC states it in amr; SAML states it in
    /// the authentication context class. Only accepted when the provider is trusted to assert it -
    /// otherwise anyone able to mint a token for that provider decides our MFA policy.
    /// </summary>
    private static bool IdpAssertedSecondFactor(ClaimsPrincipal external)
    {
        foreach (var amr in external.FindAll("amr"))
        {
            var v = amr.Value;
            if (v.Equals("mfa", StringComparison.OrdinalIgnoreCase)
                || v.Equals("otp", StringComparison.OrdinalIgnoreCase)
                || v.Equals("hwk", StringComparison.OrdinalIgnoreCase)
                || v.Equals("swk", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // SAML AuthnContextClassRef, as ITfoxtec surfaces it.
        var authnContext = external.FindFirst(ClaimTypes.AuthenticationMethod)?.Value;
        return authnContext is not null
            && (authnContext.Contains("MultiFactor", StringComparison.OrdinalIgnoreCase)
                || authnContext.Contains("TimeSyncToken", StringComparison.OrdinalIgnoreCase));
    }

    private async Task SignInAsync(ApplicationUser user, FederationProvider provider, bool rememberMe)
    {
        user.LastLoginAt = DateTime.UtcNow;
        user.LastLoginMethod = SchemeKey(provider);
        await _userManager.UpdateAsync(user);
        await _signInManager.SignInAsync(user, isPersistent: rememberMe, SchemeKey(provider));

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

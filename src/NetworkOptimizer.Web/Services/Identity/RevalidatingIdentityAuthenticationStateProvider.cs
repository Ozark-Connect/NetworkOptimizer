using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Revalidates a live Blazor Server circuit's principal on a fixed interval so credential, role, and
/// membership changes take effect on already-connected circuits (which outlive cookie/stamp checks).
/// A circuit is torn down when the user's security stamp rotates (password/role/MFA change, disable,
/// sign-out-everywhere), when the account is disabled, or when the membership version stamp advances
/// (site-membership drift) - the three signals from design docs 02 and 04.
/// </summary>
public sealed class RevalidatingIdentityAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentityOptions _options;

    public RevalidatingIdentityAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> optionsAccessor)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = optionsAccessor.Value;
    }

    /// <summary>Worst-case staleness for a live circuit (design doc 02: ~5 min, configurable).</summary>
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await ValidateAsync(userManager, authenticationState.User);
    }

    private async Task<bool> ValidateAsync(UserManager<ApplicationUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
            return false;

        // Locally disabled accounts lose their circuit immediately at the next interval.
        if (!user.IsEnabled)
            return false;

        // Security stamp covers credential/global-role/2FA/sign-out-everywhere revocation.
        if (userManager.SupportsUserSecurityStamp)
        {
            var principalStamp = principal.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
            var userStamp = await userManager.GetSecurityStampAsync(user);
            if (principalStamp != userStamp)
                return false;
        }

        // Membership version covers per-site membership/group drift (separate from the security stamp).
        var principalMembership = principal.FindFirstValue(NetOptClaims.MembershipVersion);
        if (principalMembership is not null &&
            principalMembership != user.MembershipVersion.ToString())
        {
            return false;
        }

        return true;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Revalidates a live Blazor Server circuit's principal on a fixed interval so credential, role, and
/// membership changes take effect on already-connected circuits (which outlive cookie/stamp checks).
/// A circuit is torn down when the user's security stamp rotates (password/MFA change, disable, sign
/// out everywhere) or when the account is disabled. A change to what the account may do - a global
/// role or a site membership - is not a reason to throw anyone out: the membership version advancing
/// sends the circuit through a cookie refresh instead, and it lands back where it was with the new
/// permissions in hand (design docs 02 and 04).
/// </summary>
public sealed class RevalidatingIdentityAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentityOptions _options;
    private readonly NavigationManager _navigation;

    /// <summary>
    /// True once a membership refresh has been asked for and not yet taken effect. One circuit, one
    /// chance: see the same mismatch on the next tick and the refresh is not being carried out.
    /// </summary>
    private bool _membershipRefreshAsked;

    private readonly ILogger<RevalidatingIdentityAuthenticationStateProvider> _logger;

    public RevalidatingIdentityAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> optionsAccessor,
        NavigationManager navigation)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = optionsAccessor.Value;
        _navigation = navigation;
        _logger = loggerFactory.CreateLogger<RevalidatingIdentityAuthenticationStateProvider>();
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

        // Security stamp covers credential/global-role/2FA/sign-out-everywhere revocation - but the
        // stamp on THIS PRINCIPAL is not the same question as the stamp on the browser's cookie, and
        // only the cookie decides whether the session is still good. Signing out everywhere rotates
        // the stamp and hands the browser that asked a fresh cookie; its live circuit still holds the
        // principal captured before that, so killing the circuit on the mismatch booted the very
        // session the refresh existed to preserve - five minutes later, which is a mystery to whoever
        // it happens to. Send it to the endpoint instead: that sees the real cookie, refuses a
        // genuinely revoked one, and lands a good one back where it was with a current principal.
        if (userManager.SupportsUserSecurityStamp)
        {
            var principalStamp = principal.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
            var userStamp = await userManager.GetSecurityStampAsync(user);
            if (principalStamp != userStamp)
            {
                Revalidate();
                return true;
            }
        }

        // Permissions changed since this cookie was issued. The account is still valid - only what it
        // may do has moved - so the session is refreshed rather than dropped. Returning false here
        // would leave the circuit anonymous while the cookie stayed good, which reads to the user as
        // being locked out of every page with no way back except signing out and in again.
        var principalMembership = principal.FindFirstValue(NetOptClaims.MembershipVersion);
        if (principalMembership is not null &&
            principalMembership != user.MembershipVersion.ToString())
        {
            // ...but only once. The refresh above is a navigation the CLIENT has to carry out, so on
            // its own it is a request, not enforcement: a client that drops the command keeps a live
            // circuit whose captured principal still carries the old roles, and every later tick took
            // this same return-true path, so nothing ever caught up with it. A demoted Admin could
            // hold Admin indefinitely that way. An honest client completes the navigation, which ends
            // this circuit, so it never reaches a second tick still mismatched - seeing the SAME
            // mismatch twice running means the refresh is not happening, and then the session is
            // dropped rather than asked again. A tab that merely failed to navigate (throttled in the
            // background, say) recovers with a reload.
            if (_membershipRefreshAsked)
            {
                _logger?.LogWarning(
                    "Circuit for {User} still reports membership version {Stale} after a refresh was "
                    + "issued; ending the session rather than asking again.",
                    user.UserName, principalMembership);
                return false;
            }

            _membershipRefreshAsked = true;
            var returnUrl = "/" + _navigation.ToBaseRelativePath(_navigation.Uri);
            _navigation.NavigateTo(
                $"/api/account/session?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
        }
        else
        {
            // Back in step - either the refresh landed or the change was reverted.
            _membershipRefreshAsked = false;
        }

        return true;
    }

    /// <summary>Hands the decision to the endpoint, which is the only place the real cookie is seen.</summary>
    private void Revalidate()
    {
        var returnUrl = "/" + _navigation.ToBaseRelativePath(_navigation.Uri);
        _navigation.NavigateTo(
            $"/api/account/revalidate?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
    }
}

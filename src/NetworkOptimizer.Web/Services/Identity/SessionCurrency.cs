using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Whether a cookie still speaks for its account right now. The cookie handler re-checks the security
/// stamp only on an interval (5 min), so a revoked cookie keeps authenticating until then. Anything that
/// acts for the user or re-issues their cookie inside that window must ask this first, or a session
/// that was signed out everywhere, disabled, or had its password changed gets a fresh cookie back.
/// </summary>
public static class SessionCurrency
{
    public static async Task<bool> IsCurrentAsync(
        ClaimsPrincipal principal,
        ApplicationUser? user,
        UserManager<ApplicationUser> userManager,
        IdentityOptions options)
    {
        if (user is null || !user.IsEnabled)
            return false;

        if (!userManager.SupportsUserSecurityStamp)
            return true;

        var onCookie = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
        return onCookie == await userManager.GetSecurityStampAsync(user);
    }
}

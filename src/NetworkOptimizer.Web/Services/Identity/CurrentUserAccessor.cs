using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Resolves the current <see cref="ApplicationUser"/> from a principal, so pages and components can get
/// their user record without referencing <see cref="UserManager{TUser}"/> directly (keeping identity
/// manager access confined per architecture test A3).
/// </summary>
public interface ICurrentUserAccessor
{
    Task<ApplicationUser?> GetAsync(ClaimsPrincipal principal);
}

/// <inheritdoc />
public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CurrentUserAccessor(UserManager<ApplicationUser> userManager) => _userManager = userManager;

    public Task<ApplicationUser?> GetAsync(ClaimsPrincipal principal) => _userManager.GetUserAsync(principal)!;
}

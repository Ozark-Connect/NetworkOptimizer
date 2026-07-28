using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// The one identity fact the sign-in page needs before anyone is signed in. It lives here rather than on
/// <see cref="IIdentityAdminService"/> because that interface is gated, and every gate on it denies the
/// anonymous caller a login page runs as - correctly so. Deliberately narrow: a boolean, not a roster,
/// and a plain row count rather than <c>UserManager</c>, which stays with identity infrastructure.
/// </summary>
public sealed class IdentityLoginFacts
{
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;

    public IdentityLoginFacts(IDbContextFactory<AuthDbContext> authDbFactory)
        => _authDbFactory = authDbFactory;

    /// <summary>
    /// True while the install has at most one account - the migration/first-run state the sign-in page
    /// pre-fills "admin" for. Once a second user exists it stops, so a Viewer or Operator is not handed
    /// someone else's username to clear.
    /// </summary>
    public async Task<bool> IsSingleAccountInstallAsync()
    {
        await using var db = await _authDbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().CountAsync() <= 1;
    }
}

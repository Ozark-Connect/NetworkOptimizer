using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Display names for stored usernames (a plan's applier, an approver): the user's display name,
/// else the username itself, as the rest of the app shows people.
/// </summary>
public static class UserDisplayNames
{
    /// <summary>Each username mapped to how it is shown; a name with no matching user maps to itself.</summary>
    public static async Task<Dictionary<string, string>> ResolveAsync(IServiceScopeFactory scopes, IEnumerable<string> userNames,
        CancellationToken ct = default)
    {
        var names = userNames.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = names.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;

        using var scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<UserManager<ApplicationUser>>() is not { } users) return result;
        var upper = names.Select(n => n.ToUpperInvariant()).ToList();
        foreach (var u in await users.Users.Where(u => u.NormalizedUserName != null && upper.Contains(u.NormalizedUserName))
                     .Select(u => new { u.UserName, u.DisplayName }).ToListAsync(ct))
            if (u.UserName != null && !string.IsNullOrWhiteSpace(u.DisplayName))
                result[u.UserName] = u.DisplayName;
        return result;
    }
}

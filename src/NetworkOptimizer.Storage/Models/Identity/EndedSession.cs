using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// A signed-out login. The application cookie is a self-contained ticket, so deleting it from the
/// browser that signed out does nothing to a copy of it elsewhere; this row is what the server checks
/// on every request to refuse that copy. One login, not one user: signing out here leaves the user's
/// other browsers signed in, which is what sign out everywhere (the security stamp) is for.
/// </summary>
public class EndedSession
{
    /// <summary>The login's id, the <c>netopt:login</c> claim. Stable for the whole login, refreshes included.</summary>
    [Key]
    [MaxLength(32)]
    public string LoginId { get; set; } = string.Empty;

    /// <summary>The Identity user the login belonged to (<see cref="ApplicationUser.Id"/>).</summary>
    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public DateTime EndedAt { get; set; }

    /// <summary>When no cookie for this login could still be valid, so the row can go.</summary>
    public DateTime ExpiresAt { get; set; }
}

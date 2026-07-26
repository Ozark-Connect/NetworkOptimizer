using Microsoft.AspNetCore.Identity;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Global role. Extends Identity's <see cref="IdentityRole"/> with a description and a per-role
/// "Require MFA" policy (design doc 02): when set, users in this role must have MFA enrolled,
/// enforced at login as step-up-to-enrollment rather than a nag banner. The canonical global role
/// names are in <see cref="Roles"/>.
/// </summary>
public class ApplicationRole : IdentityRole
{
    /// <summary>Human-readable description of what the role grants.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// When true, members of this role must have a second factor enrolled; login redirects an
    /// unenrolled member into MFA enrollment before completing the session.
    /// </summary>
    public bool RequireMfa { get; set; }

    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }
}

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Canonical global role names (design doc 04). Deliberately small: Admin / Operator / Viewer.
/// Stored as Identity role claims on the principal. Per-site elevation is separate (see
/// <see cref="SiteMembership"/> and <see cref="SiteRole"/>).
/// </summary>
public static class GlobalRoles
{
    /// <summary>Everything: user/role management, federation config, licensing, all sites, destructive ops, audit read.</summary>
    public const string Admin = "Admin";

    /// <summary>Operate everything on permitted sites; no Settings, no user management, no federation config.</summary>
    public const string Operator = "Operator";

    /// <summary>Read-only on permitted sites.</summary>
    public const string Viewer = "Viewer";

    /// <summary>All global roles, most-privileged first.</summary>
    public static readonly string[] All = { Admin, Operator, Viewer };
}

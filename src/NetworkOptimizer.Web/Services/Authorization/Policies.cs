using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Canonical authorization policy names (design doc 04). Global policies gate cross-site and
/// admin surfaces; site-scoped policies are resource-based - the site slug is the resource passed to
/// <c>IAuthorizationService.AuthorizeAsync(user, slug, policy)</c>.
/// </summary>
public static class Policies
{
    /// <summary>Global Admin only.</summary>
    public const string RequireAdmin = "RequireAdmin";

    /// <summary>Global Operator or Admin.</summary>
    public const string RequireOperator = "RequireOperator";

    /// <summary>Any authenticated user (Viewer, Operator, or Admin).</summary>
    public const string RequireViewer = "RequireViewer";

    /// <summary>
    /// Signed in, and nothing more - the caller's own account surface. Distinct from
    /// <see cref="RequireViewer"/> because it admits a session that still owes the install a second
    /// factor, which is the one thing such a session is allowed to do.
    /// </summary>
    public const string AccountSelfService = "AccountSelfService";

    /// <summary>
    /// Reaching Settings: global Admin anywhere, or Site Admin on the managed site in context.
    /// </summary>
    public const string ManageSettings = "ManageSettings";

    /// <summary>Site-scoped: at least SiteViewer on the resource site.</summary>
    public const string SiteViewer = "SiteViewer";

    /// <summary>Site-scoped: at least SiteOperator on the resource site.</summary>
    public const string SiteOperator = "SiteOperator";

    /// <summary>Site-scoped: SiteAdmin on the resource site.</summary>
    public const string SiteAdmin = "SiteAdmin";

    /// <summary>Maps a minimum <see cref="SiteRole"/> to its site-scoped policy name.</summary>
    public static string ForSiteRole(SiteRole minimum) => minimum switch
    {
        SiteRole.SiteAdmin => SiteAdmin,
        SiteRole.SiteOperator => SiteOperator,
        _ => SiteViewer,
    };
}

using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Instance-wide authentication/authorization policy toggles, stored as GLOBAL settings in the main
/// database. Kept small and explicit so login and site-scoping code can read them without reaching
/// into the raw settings store.
/// </summary>
public interface IAuthPolicyOptions
{
    /// <summary>
    /// SSO-only mode: local username/password login is refused (design doc 02). Safe only because the
    /// <see cref="BreakGlass"/> recovery boot always re-enables local Admin login.
    /// </summary>
    Task<bool> IsLocalLoginDisabledAsync();

    // The setters are NOT here. This interface is read by the sign-in page as an anonymous caller and
    // by the authorization handlers on every check, so it cannot be gated - and leaving a setter on it
    // meant anything holding it could flip the two settings that decide who gets in and which sites a
    // role reaches, with no service-tier check at all. They live on IAuthPolicyAdminService below.

    /// <summary>
    /// When on, Operators and Viewers reach only the sites they are granted; when off, a global role
    /// applies across every site. Design doc 04. On by default - a grant that cannot confine anyone is
    /// not really a grant. This applies on a single-site install too, where it is how an admin picks
    /// who may reach the main site at all.
    /// </summary>
    Task<bool> IsRestrictSitesToMembersAsync();

}

/// <summary>
/// Changing the two instance-wide authentication policies (design doc 06, gate 9).
///
/// Separate from <see cref="IAuthPolicyOptions"/> because that one has to stay ungated - the login
/// page reads it before anyone is authenticated, and every authorization check reads it - while these
/// two writes are among the most powerful in the product. Turning the site restriction OFF hands
/// every global Viewer and Operator every site at once, and turning local login off decides whether
/// passwords work at all. Both are global Admin and both are audited.
/// </summary>
[MutatingService]
public interface IAuthPolicyAdminService
{
    /// <summary>Sets the SSO-only local-login toggle.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "auth_policy", InstanceScoped = true)]
    Task SetLocalLoginDisabledAsync(bool disabled);

    /// <summary>Sets the per-site restriction toggle.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "auth_policy", InstanceScoped = true)]
    Task SetRestrictSitesToMembersAsync(bool restrict);
}

/// <inheritdoc />
public sealed class AuthPolicyOptions : IAuthPolicyOptions, IAuthPolicyAdminService
{
    private const string LocalLoginDisabledKey = "auth.local_login_disabled";
    private const string RestrictSitesToMembersKey = "auth.restrict_sites_to_members";

    private readonly ISystemSettingsService _settings;
    private readonly Authorization.SiteRoleCacheTokens _siteRoleCache;
    private readonly SiteRegistryChangeNotifier _siteRegistryChanges;

    public AuthPolicyOptions(
        ISystemSettingsService settings,
        Authorization.SiteRoleCacheTokens siteRoleCache,
        SiteRegistryChangeNotifier siteRegistryChanges)
    {
        _settings = settings;
        _siteRoleCache = siteRoleCache;
        _siteRegistryChanges = siteRegistryChanges;
    }

    /// <inheritdoc />
    public async Task<bool> IsLocalLoginDisabledAsync() => await GetBoolAsync(LocalLoginDisabledKey);

    /// <inheritdoc />
    public Task SetLocalLoginDisabledAsync(bool disabled) => SetBoolAsync(LocalLoginDisabledKey, disabled);

    /// <inheritdoc />
    public async Task<bool> IsRestrictSitesToMembersAsync()
        => await GetBoolAsync(RestrictSitesToMembersKey, defaultValue: true);

    /// <inheritdoc />
    public async Task SetRestrictSitesToMembersAsync(bool restrict)
    {
        await SetBoolAsync(RestrictSitesToMembersKey, restrict);

        // This one setting decides whether a global Operator or Viewer role reaches every site, so it
        // changes the answer for every non-Admin on every site at once. Effective roles and authorized
        // slug sets are cached for ten minutes, and nothing else here would drop them - toggling it
        // appeared to do nothing at all until they aged out.
        _siteRoleCache.InvalidateAll();

        // And say so, the same way granting or revoking access does. There is no per-user row to bump
        // here - the setting moves every Operator and Viewer at once - so this is the group-access
        // shape: drop the cache, then tell the live circuits their site list has moved. Without it a
        // signed-in user kept the old set of sites in front of them until they navigated.
        _siteRegistryChanges.NotifySitesChanged();
    }

    private async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var raw = await _settings.GetGlobalAsync(key);
        return string.IsNullOrEmpty(raw)
            ? defaultValue
            : string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private Task SetBoolAsync(string key, bool value)
        => _settings.SetGlobalAsync(key, value ? "true" : "false");
}

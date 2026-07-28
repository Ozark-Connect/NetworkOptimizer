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

    /// <summary>Sets the SSO-only local-login toggle.</summary>
    Task SetLocalLoginDisabledAsync(bool disabled);

    /// <summary>
    /// When on, Operators and Viewers reach only the sites they are granted; when off, a global role
    /// applies across every site. Design doc 04. On by default - a grant that cannot confine anyone is
    /// not really a grant. This applies on a single-site install too, where it is how an admin picks
    /// who may reach the main site at all.
    /// </summary>
    Task<bool> IsRestrictSitesToMembersAsync();

    /// <summary>Sets the per-site restriction toggle.</summary>
    Task SetRestrictSitesToMembersAsync(bool restrict);
}

/// <inheritdoc />
public sealed class AuthPolicyOptions : IAuthPolicyOptions
{
    private const string LocalLoginDisabledKey = "auth.local_login_disabled";
    private const string RestrictSitesToMembersKey = "auth.restrict_sites_to_members";

    private readonly ISystemSettingsService _settings;
    private readonly Authorization.SiteRoleCacheTokens _siteRoleCache;

    public AuthPolicyOptions(ISystemSettingsService settings, Authorization.SiteRoleCacheTokens siteRoleCache)
    {
        _settings = settings;
        _siteRoleCache = siteRoleCache;
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

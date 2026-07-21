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
    /// When on, non-Admin users see and touch only sites they are members of; when off (default),
    /// the global role applies across all sites (today's behaviour). Design doc 04.
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

    public AuthPolicyOptions(ISystemSettingsService settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<bool> IsLocalLoginDisabledAsync() => await GetBoolAsync(LocalLoginDisabledKey);

    /// <inheritdoc />
    public Task SetLocalLoginDisabledAsync(bool disabled) => SetBoolAsync(LocalLoginDisabledKey, disabled);

    /// <inheritdoc />
    public async Task<bool> IsRestrictSitesToMembersAsync() => await GetBoolAsync(RestrictSitesToMembersKey);

    /// <inheritdoc />
    public Task SetRestrictSitesToMembersAsync(bool restrict) => SetBoolAsync(RestrictSitesToMembersKey, restrict);

    private async Task<bool> GetBoolAsync(string key)
        => string.Equals(await _settings.GetGlobalAsync(key), "true", StringComparison.OrdinalIgnoreCase);

    private Task SetBoolAsync(string key, bool value)
        => _settings.SetGlobalAsync(key, value ? "true" : "false");
}

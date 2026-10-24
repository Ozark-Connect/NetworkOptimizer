namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Per-user UI preferences (layout choices and the like), stored as global settings keyed
/// <c>{key}:user:{userId}</c>. A user with no value of their own reads the install-wide value, so a
/// preference set before per-user storage stays everyone's default.
/// </summary>
public interface IUserPreferences
{
    /// <summary>The current caller's value for <paramref name="key"/>, else the install-wide value.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Stores the current caller's value for <paramref name="key"/>.</summary>
    Task SetAsync(string key, string? value);
}

/// <inheritdoc />
public sealed class UserPreferences : IUserPreferences
{
    private readonly ISystemSettingsService _settings;
    private readonly ICallerContext _caller;

    public UserPreferences(ISystemSettingsService settings, ICallerContext caller)
    {
        _settings = settings;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key)
    {
        if (_caller.Current?.UserId is { } userId
            && await _settings.GetGlobalAsync(UserDismissals.UserKey(key, userId)) is { } own)
            return own;
        return await _settings.GetGlobalAsync(key);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string? value)
    {
        var caller = _caller.Current;
        if (caller?.AuthenticationDisabled == true)
            await _settings.SetGlobalAsync(key, value);
        else if (caller?.UserId is { } userId)
            await _settings.SetGlobalAsync(UserDismissals.UserKey(key, userId), value);
    }
}

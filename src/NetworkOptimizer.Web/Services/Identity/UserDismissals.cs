using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Per-user "don't show this again" flags for banners and hints, stored as global settings keyed
/// <c>{key}:user:{userId}</c>. A banner one person dismisses stays visible to everyone else.
/// </summary>
public interface IUserDismissals
{
    /// <summary>True when the current caller has dismissed <paramref name="key"/>.</summary>
    Task<bool> IsDismissedAsync(string key);

    /// <summary>Records that the current caller dismissed <paramref name="key"/>.</summary>
    Task DismissAsync(string key);
}

/// <inheritdoc />
public sealed class UserDismissals : IUserDismissals
{
    private readonly ISystemSettingsService _settings;
    private readonly ICallerContext _caller;

    public UserDismissals(ISystemSettingsService settings, ICallerContext caller)
    {
        _settings = settings;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<bool> IsDismissedAsync(string key)
    {
        var caller = _caller.Current;
        if (caller?.AuthenticationDisabled == true)
            return IsTrue(await _settings.GetGlobalAsync(key));
        if (caller?.UserId is not { } userId)
            return false;

        if (IsTrue(await _settings.GetGlobalAsync(UserKey(key, userId))))
            return true;

        // The install-wide flag predates per-user storage and was set by an admin, so admins keep it.
        return caller.Principal?.IsInRole(Roles.Admin) == true
               && IsTrue(await _settings.GetGlobalAsync(key));
    }

    /// <inheritdoc />
    public async Task DismissAsync(string key)
    {
        var caller = _caller.Current;
        if (caller?.AuthenticationDisabled == true)
            await _settings.SetGlobalAsync(key, "true");
        else if (caller?.UserId is { } userId)
            await _settings.SetGlobalAsync(UserKey(key, userId), "true");
    }

    internal static string UserKey(string key, string userId) => $"{key}:user:{userId}";

    // Any stored value counts except an explicit "false": the identity intro has always stored "true"
    // and been read as "present".
    private static bool IsTrue(string? value)
        => !string.IsNullOrEmpty(value) && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}

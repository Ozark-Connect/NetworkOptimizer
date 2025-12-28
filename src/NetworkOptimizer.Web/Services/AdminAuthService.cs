using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Determines the admin password source and provides password resolution.
/// Priority: Database (if enabled) > Environment variable > Auto-generated (first run)
/// </summary>
public class AdminAuthService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<AdminAuthService> _logger;

    // Cached password and source
    private string? _cachedPassword;
    private AdminPasswordSource _cachedSource = AdminPasswordSource.None;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(30);

    // Track if we've already logged the one-time password
    private static bool _oneTimePasswordLogged = false;

    public AdminAuthService(
        ISettingsRepository settingsRepository,
        ICredentialProtectionService credentialProtection,
        ILogger<AdminAuthService> logger)
    {
        _settingsRepository = settingsRepository;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current effective admin password (decrypted).
    /// Returns null if no password is configured.
    /// </summary>
    public async Task<string?> GetEffectivePasswordAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);
        return _cachedPassword;
    }

    /// <summary>
    /// Gets the source of the current admin password.
    /// </summary>
    public async Task<AdminPasswordSource> GetPasswordSourceAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);
        return _cachedSource;
    }

    /// <summary>
    /// Validates a password against the current effective password.
    /// </summary>
    public async Task<bool> ValidatePasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        var effectivePassword = await GetEffectivePasswordAsync(cancellationToken);
        if (string.IsNullOrEmpty(effectivePassword))
        {
            _logger.LogWarning("Password validation attempted but no admin password is configured");
            return false;
        }

        var isValid = password == effectivePassword;
        if (!isValid)
        {
            _logger.LogWarning("Invalid admin password attempt. Source: {Source}", _cachedSource);
        }
        else
        {
            _logger.LogDebug("Admin password validated successfully. Source: {Source}", _cachedSource);
        }

        return isValid;
    }

    /// <summary>
    /// Checks if admin authentication is required.
    /// </summary>
    public async Task<bool> IsAuthenticationRequiredAsync(CancellationToken cancellationToken = default)
    {
        var password = await GetEffectivePasswordAsync(cancellationToken);
        return !string.IsNullOrEmpty(password);
    }

    /// <summary>
    /// Gets the admin settings from the database.
    /// </summary>
    public async Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _settingsRepository.GetAdminSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// Saves admin settings (encrypts password before saving).
    /// </summary>
    public async Task SaveAdminSettingsAsync(string? plainPassword, bool enabled, CancellationToken cancellationToken = default)
    {
        var settings = new AdminSettings
        {
            Password = string.IsNullOrEmpty(plainPassword)
                ? null
                : _credentialProtection.Encrypt(plainPassword),
            Enabled = enabled
        };

        await _settingsRepository.SaveAdminSettingsAsync(settings, cancellationToken);

        // Force cache refresh
        _lastRefresh = DateTime.MinValue;

        // Log the change
        var newSource = await GetPasswordSourceAsync(cancellationToken);
        if (enabled && !string.IsNullOrEmpty(plainPassword))
        {
            _logger.LogInformation("Admin settings updated. Source: Database (password configured and enabled)");
        }
        else if (!enabled)
        {
            _logger.LogInformation("Admin settings updated. Database password disabled. Will use environment variable if configured");
        }
        else
        {
            _logger.LogInformation("Admin settings updated. Source: {Source}", newSource);
        }
    }

    /// <summary>
    /// Clears the database admin password (falls back to env var).
    /// </summary>
    public async Task ClearDatabasePasswordAsync(CancellationToken cancellationToken = default)
    {
        var settings = new AdminSettings
        {
            Password = null,
            Enabled = false
        };

        await _settingsRepository.SaveAdminSettingsAsync(settings, cancellationToken);

        // Force cache refresh
        _lastRefresh = DateTime.MinValue;

        _logger.LogInformation("Database admin password cleared. Will use environment variable (APP_PASSWORD) if configured");
    }

    /// <summary>
    /// Logs the current authentication configuration at startup.
    /// If no password is configured, generates a one-time password and logs it.
    /// </summary>
    public async Task LogStartupConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);

        switch (_cachedSource)
        {
            case AdminPasswordSource.Database:
                _logger.LogInformation("Admin authentication enabled using database-stored password");
                break;
            case AdminPasswordSource.Environment:
                _logger.LogInformation("Admin authentication enabled using environment variable (APP_PASSWORD)");
                break;
            case AdminPasswordSource.AutoGenerated:
                // Password was auto-generated - log it prominently if first time
                if (!_oneTimePasswordLogged && !string.IsNullOrEmpty(_cachedPassword))
                {
                    _oneTimePasswordLogged = true;
                    _logger.LogWarning("========================================");
                    _logger.LogWarning("  FIRST-RUN ADMIN PASSWORD GENERATED   ");
                    _logger.LogWarning("========================================");
                    _logger.LogWarning("  Password: {Password}", _cachedPassword);
                    _logger.LogWarning("========================================");
                    _logger.LogWarning("  Use this password to log in, then    ");
                    _logger.LogWarning("  go to Settings to change it.         ");
                    _logger.LogWarning("  This password is shown ONLY ONCE.    ");
                    _logger.LogWarning("========================================");
                }
                else
                {
                    _logger.LogInformation("Admin authentication enabled using auto-generated password (see logs from first startup)");
                }
                break;
            case AdminPasswordSource.None:
                _logger.LogWarning("No admin password configured. Authentication is disabled");
                break;
        }
    }

    private async Task RefreshCacheIfNeededAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastRefresh < _cacheTimeout)
            return;

        try
        {
            var dbSettings = await _settingsRepository.GetAdminSettingsAsync(cancellationToken);

            // Check database first - only if explicitly enabled by user
            if (dbSettings?.Enabled == true && dbSettings.HasPassword)
            {
                var decryptedPassword = _credentialProtection.Decrypt(dbSettings.Password!);
                if (!string.IsNullOrEmpty(decryptedPassword))
                {
                    _cachedPassword = decryptedPassword;
                    _cachedSource = AdminPasswordSource.Database;
                    _lastRefresh = DateTime.UtcNow;
                    _logger.LogDebug("Using database-stored admin password");
                    return;
                }
            }

            // Fall back to environment variable
            var envPassword = Environment.GetEnvironmentVariable("APP_PASSWORD");
            if (!string.IsNullOrEmpty(envPassword))
            {
                _cachedPassword = envPassword;
                _cachedSource = AdminPasswordSource.Environment;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Using environment variable (APP_PASSWORD) for admin password");
                return;
            }

            // Check for auto-generated password (Enabled=false but password exists)
            if (dbSettings?.HasPassword == true)
            {
                var decryptedPassword = _credentialProtection.Decrypt(dbSettings.Password!);
                if (!string.IsNullOrEmpty(decryptedPassword))
                {
                    _cachedPassword = decryptedPassword;
                    _cachedSource = AdminPasswordSource.AutoGenerated;
                    _lastRefresh = DateTime.UtcNow;
                    _logger.LogDebug("Using auto-generated admin password");
                    return;
                }
            }

            // No password configured - generate one and store it
            var generatedPassword = GenerateSecurePassword();
            var settings = new AdminSettings
            {
                Password = _credentialProtection.Encrypt(generatedPassword),
                Enabled = false // Mark as auto-generated (not user-enabled)
            };
            await _settingsRepository.SaveAdminSettingsAsync(settings, cancellationToken);

            _cachedPassword = generatedPassword;
            _cachedSource = AdminPasswordSource.AutoGenerated;
            _lastRefresh = DateTime.UtcNow;
            _logger.LogDebug("Generated and stored new admin password");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh admin password cache");
            // Keep existing cached values on error
        }
    }

    /// <summary>
    /// Generates a secure random password (16 characters, alphanumeric)
    /// </summary>
    private static string GenerateSecurePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var password = new char[16];
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[16];
        rng.GetBytes(bytes);

        for (int i = 0; i < password.Length; i++)
        {
            password[i] = chars[bytes[i] % chars.Length];
        }

        return new string(password);
    }
}

/// <summary>
/// Source of the admin password
/// </summary>
public enum AdminPasswordSource
{
    /// <summary>No password configured (should not happen with auto-generation)</summary>
    None,
    /// <summary>Password from database (user-configured)</summary>
    Database,
    /// <summary>Password from APP_PASSWORD environment variable</summary>
    Environment,
    /// <summary>Auto-generated password on first run</summary>
    AutoGenerated
}

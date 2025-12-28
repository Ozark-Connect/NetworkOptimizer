using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Determines the admin password source and provides password resolution.
/// Priority: Database (if enabled) > Environment variable > Auto-generated (first run)
/// Passwords are stored using PBKDF2-SHA256 hashing (not reversible).
/// </summary>
public class AdminAuthService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<AdminAuthService> _logger;

    // Cached hash and source (never cache plaintext passwords)
    private string? _cachedPasswordHash;
    private AdminPasswordSource _cachedSource = AdminPasswordSource.None;
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromSeconds(30);

    // Track if we've already logged the one-time password
    private static bool _oneTimePasswordLogged = false;
    private static string? _oneTimePasswordForDisplay = null;

    public AdminAuthService(
        ISettingsRepository settingsRepository,
        IPasswordHasher passwordHasher,
        ILogger<AdminAuthService> logger)
    {
        _settingsRepository = settingsRepository;
        _passwordHasher = passwordHasher;
        _logger = logger;
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
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public async Task<bool> ValidatePasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);

        if (string.IsNullOrEmpty(_cachedPasswordHash))
        {
            _logger.LogWarning("Password validation attempted but no admin password is configured");
            return false;
        }

        bool isValid;

        // For environment variable, we compare directly (env var is plaintext)
        if (_cachedSource == AdminPasswordSource.Environment)
        {
            // Use constant-time comparison for env var too
            var envPassword = Environment.GetEnvironmentVariable("APP_PASSWORD") ?? "";
            isValid = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(password),
                System.Text.Encoding.UTF8.GetBytes(envPassword));
        }
        else
        {
            // For database passwords, verify against hash
            isValid = _passwordHasher.VerifyPassword(password, _cachedPasswordHash);
        }

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
        await RefreshCacheIfNeededAsync(cancellationToken);
        return !string.IsNullOrEmpty(_cachedPasswordHash);
    }

    /// <summary>
    /// Gets the admin settings from the database.
    /// </summary>
    public async Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _settingsRepository.GetAdminSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// Saves admin settings (hashes password before saving).
    /// </summary>
    public async Task SaveAdminSettingsAsync(string? plainPassword, bool enabled, CancellationToken cancellationToken = default)
    {
        var settings = new AdminSettings
        {
            Password = string.IsNullOrEmpty(plainPassword)
                ? null
                : _passwordHasher.HashPassword(plainPassword),
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
                _logger.LogInformation("Admin authentication enabled using database-stored password (PBKDF2-SHA256 hashed)");
                break;
            case AdminPasswordSource.Environment:
                _logger.LogInformation("Admin authentication enabled using environment variable (APP_PASSWORD)");
                break;
            case AdminPasswordSource.AutoGenerated:
                // Password was auto-generated - log it prominently if first time
                if (!_oneTimePasswordLogged && !string.IsNullOrEmpty(_oneTimePasswordForDisplay))
                {
                    _oneTimePasswordLogged = true;
                    _logger.LogWarning("========================================");
                    _logger.LogWarning("  FIRST-RUN ADMIN PASSWORD GENERATED   ");
                    _logger.LogWarning("========================================");
                    _logger.LogWarning("  Password: {Password}", _oneTimePasswordForDisplay);
                    _logger.LogWarning("========================================");
                    _logger.LogWarning("  Use this password to log in, then    ");
                    _logger.LogWarning("  go to Settings to change it.         ");
                    _logger.LogWarning("  This password is shown ONLY ONCE.    ");
                    _logger.LogWarning("========================================");

                    // Clear the plaintext after logging
                    _oneTimePasswordForDisplay = null;
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
                _cachedPasswordHash = dbSettings.Password;
                _cachedSource = AdminPasswordSource.Database;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Using database-stored admin password (hashed)");
                return;
            }

            // Fall back to environment variable
            var envPassword = Environment.GetEnvironmentVariable("APP_PASSWORD");
            if (!string.IsNullOrEmpty(envPassword))
            {
                // For env var, we store a marker - validation handles it specially
                _cachedPasswordHash = "__ENV__";
                _cachedSource = AdminPasswordSource.Environment;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Using environment variable (APP_PASSWORD) for admin password");
                return;
            }

            // Check for auto-generated password (Enabled=false but password exists)
            if (dbSettings?.HasPassword == true)
            {
                _cachedPasswordHash = dbSettings.Password;
                _cachedSource = AdminPasswordSource.AutoGenerated;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Using auto-generated admin password (hashed)");
                return;
            }

            // No password configured - generate one, show it, then hash and store it
            var generatedPassword = GenerateSecurePassword();

            // Store plaintext temporarily for one-time display
            _oneTimePasswordForDisplay = generatedPassword;

            // Hash and store
            var hashedPassword = _passwordHasher.HashPassword(generatedPassword);
            var settings = new AdminSettings
            {
                Password = hashedPassword,
                Enabled = false // Mark as auto-generated (not user-enabled)
            };
            await _settingsRepository.SaveAdminSettingsAsync(settings, cancellationToken);

            _cachedPasswordHash = hashedPassword;
            _cachedSource = AdminPasswordSource.AutoGenerated;
            _lastRefresh = DateTime.UtcNow;
            _logger.LogDebug("Generated and stored new admin password (hashed)");
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
        // Exclude ambiguous characters (0, O, l, 1, I)
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
    /// <summary>Password from database (user-configured, hashed)</summary>
    Database,
    /// <summary>Password from APP_PASSWORD environment variable</summary>
    Environment,
    /// <summary>Auto-generated password on first run (hashed)</summary>
    AutoGenerated
}

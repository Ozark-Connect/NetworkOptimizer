using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Result of password validation
/// </summary>
public record PasswordValidationResult(bool IsValid, string? ErrorMessage = null);

/// <summary>
/// Interface for admin authentication service
/// </summary>
public interface IAdminAuthService
{
    Task<AdminPasswordSource> GetPasswordSourceAsync(CancellationToken cancellationToken = default);
    Task<bool> ValidatePasswordAsync(string password, CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticationRequiredAsync(CancellationToken cancellationToken = default);
    Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveAdminSettingsAsync(string? plainPassword, bool enabled, CancellationToken cancellationToken = default);
    Task ClearDatabasePasswordAsync(CancellationToken cancellationToken = default);
    Task LogStartupConfigurationAsync(CancellationToken cancellationToken = default);
    PasswordValidationResult ValidateNewPassword(string password, string confirmPassword);
}

/// <summary>
/// Determines the admin password source and provides password resolution.
/// Priority: Database (if enabled) > Environment variable > Auto-generated (first run)
/// Passwords are stored using PBKDF2-SHA256 hashing (not reversible).
/// </summary>
public class AdminAuthService : IAdminAuthService
{
    // The admin login is instance-wide, so admin settings always live in the
    // main database - never the scoped, site-routed context (which would report
    // a per-site "auto-generated" password and even write one into each managed
    // site's database).
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AdminAuthCache _cache;
    private readonly ILogger<AdminAuthService> _logger;

    public AdminAuthService(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        IPasswordHasher passwordHasher,
        AdminAuthCache cache,
        ILogger<AdminAuthService> logger)
    {
        _mainDbFactory = mainDbFactory;
        _passwordHasher = passwordHasher;
        _cache = cache;
        _logger = logger;
    }

    private async Task<AdminSettings?> GetAdminSettingsFromMainAsync(CancellationToken cancellationToken)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AdminSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    }

    private async Task SaveAdminSettingsToMainAsync(AdminSettings settings, CancellationToken cancellationToken)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AdminSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing != null)
        {
            existing.Password = settings.Password;
            existing.Enabled = settings.Enabled;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            settings.CreatedAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            db.AdminSettings.Add(settings);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the source of the current admin password.
    /// </summary>
    public async Task<AdminPasswordSource> GetPasswordSourceAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);
        return _cache.Source;
    }

    /// <summary>
    /// Validates a password against the current effective password.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public async Task<bool> ValidatePasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);

        if (string.IsNullOrEmpty(_cache.PasswordHash))
        {
            _logger.LogWarning("Password validation attempted but no admin password is configured");
            return false;
        }

        bool isValid;

        // For environment variable, we compare directly (env var is plaintext)
        if (_cache.Source == AdminPasswordSource.Environment)
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
            isValid = _passwordHasher.VerifyPassword(password, _cache.PasswordHash);
        }

        if (!isValid)
        {
            _logger.LogWarning("Invalid admin password attempt. Source: {Source}", _cache.Source);
        }
        else
        {
            _logger.LogDebug("Admin password validated successfully. Source: {Source}", _cache.Source);
        }

        return isValid;
    }

    /// <summary>
    /// Checks if admin authentication is required.
    /// </summary>
    public async Task<bool> IsAuthenticationRequiredAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);
        return !string.IsNullOrEmpty(_cache.PasswordHash);
    }

    /// <summary>
    /// Gets the admin settings from the database.
    /// </summary>
    public async Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAdminSettingsFromMainAsync(cancellationToken);
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

        await SaveAdminSettingsToMainAsync(settings, cancellationToken);

        // Force cache refresh
        _cache.Invalidate();

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
    /// Validates a new password meets complexity requirements.
    /// </summary>
    public PasswordValidationResult ValidateNewPassword(string password, string confirmPassword)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordValidationResult(false, "Please enter a new password");

        if (password.Length < 8)
            return new PasswordValidationResult(false, "Password must be at least 8 characters");

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return new PasswordValidationResult(false, "Password must contain at least one letter and one number");

        if (password != confirmPassword)
            return new PasswordValidationResult(false, "Passwords do not match");

        return new PasswordValidationResult(true);
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

        await SaveAdminSettingsToMainAsync(settings, cancellationToken);

        // Force cache refresh
        _cache.Invalidate();

        _logger.LogInformation("Database admin password cleared. Will use environment variable (APP_PASSWORD) if configured");
    }

    /// <summary>
    /// Logs the current authentication configuration at startup.
    /// </summary>
    public async Task LogStartupConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCacheIfNeededAsync(cancellationToken);

        switch (_cache.Source)
        {
            case AdminPasswordSource.Database:
                _logger.LogInformation("Admin authentication enabled using database-stored password");
                break;
            case AdminPasswordSource.Environment:
                _logger.LogInformation("Admin authentication enabled using environment variable (APP_PASSWORD)");
                break;
            case AdminPasswordSource.AutoGenerated:
                // Password is logged immediately when generated in RefreshCacheIfNeededAsync
                _logger.LogInformation("Admin authentication enabled using auto-generated password");
                break;
            case AdminPasswordSource.None:
                _logger.LogWarning("No admin password configured. Authentication is disabled");
                break;
        }
    }

    private async Task RefreshCacheIfNeededAsync(CancellationToken cancellationToken)
    {
        // Cache hit: nothing to do, nothing to log.
        if (_cache.IsFresh)
            return;

        await _cache.RefreshGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the gate: another request may have refreshed while we waited.
            if (_cache.IsFresh)
                return;

            var dbSettings = await GetAdminSettingsFromMainAsync(cancellationToken);

            // Check database first - only if explicitly enabled by user
            if (dbSettings?.Enabled == true && dbSettings.HasPassword)
            {
                StoreSource(dbSettings.Password, AdminPasswordSource.Database, "Using database-stored admin password");
                return;
            }

            // Fall back to environment variable
            var envPassword = Environment.GetEnvironmentVariable("APP_PASSWORD");
            if (!string.IsNullOrEmpty(envPassword))
            {
                // For env var, we store a marker - validation handles it specially
                StoreSource("__ENV__", AdminPasswordSource.Environment, "Using environment variable (APP_PASSWORD) for admin password");
                return;
            }

            // Check for auto-generated password (Enabled=false but password exists)
            if (dbSettings?.HasPassword == true)
            {
                StoreSource(dbSettings.Password, AdminPasswordSource.AutoGenerated, "Using auto-generated admin password");
                return;
            }

            // No password configured - generate one, show it, then hash and store it
            var generatedPassword = GenerateSecurePassword();

            // Log the password immediately (don't wait for LogStartupConfigurationAsync)
            _logger.LogWarning("========================================");
            _logger.LogWarning("  AUTO-GENERATED ADMIN PASSWORD         ");
            _logger.LogWarning("========================================");
            _logger.LogWarning("  Password: {Password}", generatedPassword);
            _logger.LogWarning("========================================");
            _logger.LogWarning("  Use this password to log in, then    ");
            _logger.LogWarning("  go to Settings to change it.         ");
            _logger.LogWarning("========================================");

            // Hash and store
            var hashedPassword = _passwordHasher.HashPassword(generatedPassword);
            var settings = new AdminSettings
            {
                Password = hashedPassword,
                Enabled = false // Mark as auto-generated (not user-enabled)
            };
            await SaveAdminSettingsToMainAsync(settings, cancellationToken);

            _cache.Store(hashedPassword, AdminPasswordSource.AutoGenerated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh admin password cache");
            // Keep existing cached values on error
        }
        finally
        {
            _cache.RefreshGate.Release();
        }
    }

    /// <summary>
    /// Stores a resolved password source in the shared cache, logging the source only
    /// when it actually changes (startup or a password change) - never on the periodic
    /// 30s re-resolve, so steady-state auth checks stay silent.
    /// </summary>
    private void StoreSource(string? passwordHash, AdminPasswordSource source, string debugMessage)
    {
        if (_cache.Source != source)
            _logger.LogDebug("{Message}", debugMessage);
        _cache.Store(passwordHash, source);
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

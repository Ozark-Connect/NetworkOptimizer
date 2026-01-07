using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NetworkOptimizer.Storage.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Interface for JWT token operations
/// </summary>
public interface IJwtService
{
    Task<string> GenerateTokenAsync(string username = "admin");
    Task<ClaimsPrincipal?> ValidateTokenAsync(string token);
    Task<TokenValidationParameters> GetTokenValidationParametersAsync();
}

/// <summary>
/// Handles JWT token generation and validation for authentication.
/// Stores the signing key securely in the database and caches it for performance.
/// </summary>
public class JwtService : IJwtService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JwtService> _logger;

    private const string Issuer = "NetworkOptimizer";
    private const string Audience = "NetworkOptimizer";
    private const int TokenExpirationMinutes = 60 * 24 * 30; // 30 days (single-user app)
    private const string SecretKeySettingName = "JwtSecretKey";

    private string? _cachedSecretKey;

    public JwtService(IServiceProvider serviceProvider, ILogger<JwtService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Generates a JWT token for the specified user with Admin role.
    /// </summary>
    /// <param name="username">The username to include in the token claims.</param>
    /// <returns>A signed JWT token string valid for 24 hours.</returns>
    public async Task<string> GenerateTokenAsync(string username = "admin")
    {
        var key = await GetOrCreateSecretKeyAsync();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(TokenExpirationMinutes),
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        _logger.LogDebug("Generated JWT token for user {Username}, expires in {Minutes} minutes", username, TokenExpirationMinutes);

        return tokenString;
    }

    /// <summary>
    /// Validates a JWT token and extracts the claims principal.
    /// </summary>
    /// <param name="token">The JWT token string to validate.</param>
    /// <returns>The claims principal if valid, or null if the token is invalid or expired.</returns>
    public async Task<ClaimsPrincipal?> ValidateTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var key = await GetOrCreateSecretKeyAsync();
        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var principal = tokenHandler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            }, out var validatedToken);

            _logger.LogDebug("JWT token validated successfully");
            return principal;
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogDebug("JWT token has expired");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JWT token validation failed");
            return null;
        }
    }

    /// <summary>
    /// Gets token validation parameters configured for ASP.NET Core authentication middleware.
    /// </summary>
    /// <returns>Configured <see cref="TokenValidationParameters"/> for JWT validation.</returns>
    public async Task<TokenValidationParameters> GetTokenValidationParametersAsync()
    {
        var key = await GetOrCreateSecretKeyAsync();

        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    }

    /// <summary>
    /// Get or create the secret key for JWT signing
    /// </summary>
    private async Task<string> GetOrCreateSecretKeyAsync()
    {
        // Return cached key if available
        if (!string.IsNullOrEmpty(_cachedSecretKey))
            return _cachedSecretKey;

        using var scope = _serviceProvider.CreateScope();
        var settingsRepo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();

        // Try to get existing key
        var existingKey = await settingsRepo.GetSystemSettingAsync(SecretKeySettingName);
        if (!string.IsNullOrEmpty(existingKey))
        {
            _cachedSecretKey = existingKey;
            _logger.LogDebug("Using existing JWT secret key from database");
            return existingKey;
        }

        // Generate new key (256 bits = 32 bytes, base64 encoded)
        var keyBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(keyBytes);
        }
        var newKey = Convert.ToBase64String(keyBytes);

        // Store in database
        await settingsRepo.SaveSystemSettingAsync(SecretKeySettingName, newKey);
        _cachedSecretKey = newKey;

        _logger.LogInformation("Generated and stored new JWT secret key");
        return newKey;
    }
}

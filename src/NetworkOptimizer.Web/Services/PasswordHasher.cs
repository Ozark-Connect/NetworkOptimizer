using System.Security.Cryptography;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Secure password hashing using PBKDF2-SHA256.
/// Passwords are one-way hashed (not reversible).
/// Format: {iterations}.{salt_base64}.{hash_base64}
/// </summary>
public class PasswordHasher : IPasswordHasher
{
    // OWASP recommended: 600,000 iterations for PBKDF2-SHA256 (2023)
    // https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html
    private const int Iterations = 600_000;
    private const int SaltSize = 16; // 128 bits
    private const int HashSize = 32; // 256 bits

    /// <summary>
    /// Hash a password using PBKDF2-SHA256 with random salt.
    /// </summary>
    public string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be empty", nameof(password));

        // Generate random salt
        var salt = new byte[SaltSize];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        // Hash password with PBKDF2-SHA256
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        // Format: iterations.salt.hash
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verify a password against a stored hash using constant-time comparison.
    /// </summary>
    public bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        try
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 3)
                return false;

            var iterations = int.Parse(parts[0]);
            var salt = Convert.FromBase64String(parts[1]);
            var expectedHash = Convert.FromBase64String(parts[2]);

            // Hash the input password with same parameters
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            // Constant-time comparison to prevent timing attacks
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            // Any parsing error = invalid hash format
            return false;
        }
    }

    /// <summary>
    /// Check if a hash needs to be rehashed (e.g., iteration count increased).
    /// </summary>
    public bool NeedsRehash(string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return true;

        try
        {
            var parts = storedHash.Split('.');
            if (parts.Length != 3)
                return true;

            var iterations = int.Parse(parts[0]);
            return iterations < Iterations;
        }
        catch
        {
            return true;
        }
    }
}

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string storedHash);
    bool NeedsRehash(string storedHash);
}

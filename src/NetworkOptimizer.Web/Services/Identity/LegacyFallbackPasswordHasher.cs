using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Identity password hasher that adds a one-release belt-and-suspenders fallback: if a stored hash
/// is still in the legacy dotted PBKDF2 format (<see cref="LegacyPasswordTranscoder.IsLegacyFormat"/>),
/// verify it against the old scheme and return <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>
/// so Identity transparently re-derives a V3 hash on next sign-in (design doc 02, migration step 2).
/// New hashes and already-transcoded hashes flow through the standard <see cref="PasswordHasher{TUser}"/>.
/// Removed with the session bridge after one release.
/// </summary>
public sealed class LegacyFallbackPasswordHasher : PasswordHasher<ApplicationUser>
{
    public LegacyFallbackPasswordHasher(IOptions<PasswordHasherOptions>? optionsAccessor = null)
        : base(optionsAccessor)
    {
    }

    /// <inheritdoc />
    public override PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user, string hashedPassword, string providedPassword)
    {
        if (LegacyPasswordTranscoder.IsLegacyFormat(hashedPassword))
        {
            return VerifyLegacy(hashedPassword, providedPassword)
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Failed;
        }

        return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    /// <summary>
    /// Verifies a plaintext against a legacy <c>{iterations}.{salt_b64}.{hash_b64}</c> PBKDF2-SHA256
    /// hash using constant-time comparison. Mirrors the original <see cref="PasswordHasher"/> logic.
    /// </summary>
    private static bool VerifyLegacy(string storedHash, string password)
    {
        try
        {
            var parts = storedHash.Split('.');
            var iterations = int.Parse(parts[0]);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}

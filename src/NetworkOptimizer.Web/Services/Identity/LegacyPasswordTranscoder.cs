using System.Buffers.Binary;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Transcodes the app's historical PBKDF2 password hash (<c>{iterations}.{salt_b64}.{hash_b64}</c>,
/// see <see cref="PasswordHasher"/>) into ASP.NET Core Identity's self-describing V3 binary layout,
/// byte-for-byte, without the plaintext. Used once at migration time so the migrated <c>admin</c>
/// user's stored hash verifies against the real <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>
/// with no re-login (design doc 02, migration step 2).
/// </summary>
/// <remarks>
/// The legacy scheme is PBKDF2-HMAC-SHA256, 16-byte salt, 32-byte subkey. Identity's V3 format is
/// <c>{ 0x01, prf:UInt32, iterCount:UInt32, saltLen:UInt32, salt, subkey }</c> with the UInt32s in
/// network (big-endian) byte order. Because the primitive is identical, only the framing changes.
/// </remarks>
public static class LegacyPasswordTranscoder
{
    // KeyDerivationPrf.HMACSHA256 == 1 (matches the legacy scheme's PRF).
    private const uint PrfHmacSha256 = 1;
    private const byte IdentityV3Marker = 0x01;

    /// <summary>
    /// Returns true if <paramref name="storedHash"/> looks like the legacy dotted PBKDF2 format
    /// (three dot-separated parts, the first being an integer iteration count). Identity V3 hashes
    /// are base64 blobs with no dots, so this cleanly distinguishes the two.
    /// </summary>
    public static bool IsLegacyFormat(string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;

        var parts = storedHash.Split('.');
        return parts.Length == 3 && int.TryParse(parts[0], out _);
    }

    /// <summary>
    /// Transcodes a legacy dotted PBKDF2-SHA256 hash into an Identity V3 hash string
    /// (base64 of the V3 binary layout). No plaintext is required or derived.
    /// </summary>
    /// <exception cref="FormatException">The input is not a valid legacy hash.</exception>
    public static string TranscodeToIdentityV3(string legacyHash)
    {
        if (!IsLegacyFormat(legacyHash))
            throw new FormatException("Input is not a legacy dotted PBKDF2 hash.");

        var parts = legacyHash.Split('.');
        var iterations = uint.Parse(parts[0]);
        var salt = Convert.FromBase64String(parts[1]);
        var subkey = Convert.FromBase64String(parts[2]);

        var output = new byte[13 + salt.Length + subkey.Length];
        output[0] = IdentityV3Marker;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1, 4), PrfHmacSha256);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(5, 4), iterations);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(9, 4), (uint)salt.Length);
        salt.CopyTo(output.AsSpan(13));
        subkey.CopyTo(output.AsSpan(13 + salt.Length));

        return Convert.ToBase64String(output);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// The session encryption newer Zyxel ZCFG firmware wraps its web API in. The client makes
/// a random AES-256 key, sends it with the login RSA-encrypted under the router's public key,
/// and from then on every request and response body is an AES-CBC envelope:
/// <c>{"content": base64, "iv": base64, "key": base64 (login only)}</c>.
/// </summary>
/// <remarks>
/// The web UI's quirks, which the router expects exactly: the IV is 32 random bytes, of which
/// only the first 16 are used for CBC; and the RSA plaintext is the Base64 text of the AES
/// key, not the raw key bytes.
/// </remarks>
[VendorSpecific("Zyxel", "RSA-wrapped AES-256-CBC envelope used by the ZCFG web UI")]
internal static class ZyxelDalCrypto
{
    /// <summary>A fresh 256-bit session key.</summary>
    public static byte[] NewAesKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Encrypt a login body: the JSON under <paramref name="aesKey"/>, and the key itself
    /// under the router's RSA public key.
    /// </summary>
    public static string EncryptLogin(string json, byte[] aesKey, string rsaPublicKey)
    {
        var iv = RandomNumberGenerator.GetBytes(32);
        using var aes = Aes.Create();
        aes.Key = aesKey;
        var content = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), iv.AsSpan(0, 16), PaddingMode.PKCS7);

        using var rsa = ImportPublicKey(rsaPublicKey);
        var wrappedKey = rsa.Encrypt(Encoding.ASCII.GetBytes(Convert.ToBase64String(aesKey)), RSAEncryptionPadding.Pkcs1);

        return JsonSerializer.Serialize(new
        {
            content = Convert.ToBase64String(content),
            key = Convert.ToBase64String(wrappedKey),
            iv = Convert.ToBase64String(iv),
        });
    }

    /// <summary>True when a response body is an encrypted envelope rather than plain JSON.</summary>
    public static bool IsEnvelope(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String &&
        body.TryGetProperty("iv", out var iv) && iv.ValueKind == JsonValueKind.String;

    /// <summary>
    /// Decrypt an envelope to its JSON text. Throws <see cref="CryptographicException"/> when
    /// the key does not match, which means the session is no longer the one the key belongs to.
    /// </summary>
    public static string Decrypt(JsonElement envelope, byte[] aesKey)
    {
        var iv = Convert.FromBase64String(envelope.GetProperty("iv").GetString()!);
        var content = Convert.FromBase64String(envelope.GetProperty("content").GetString()!);
        if (iv.Length < 16)
            throw new CryptographicException("Envelope IV is shorter than one AES block.");

        using var aes = Aes.Create();
        aes.Key = aesKey;
        var raw = aes.DecryptCbc(content, iv.AsSpan(0, 16), PaddingMode.None);
        return Encoding.UTF8.GetString(raw, 0, UnpaddedLength(raw));
    }

    /// <summary>
    /// Length without padding. Some firmware pads with NULs instead of PKCS7, so a tail that is
    /// not valid PKCS7 is stripped of trailing zeros instead.
    /// </summary>
    private static int UnpaddedLength(byte[] raw)
    {
        if (raw.Length == 0)
            return 0;

        int pad = raw[^1];
        if (pad is >= 1 and <= 16 && pad <= raw.Length && raw.AsSpan(raw.Length - pad).IndexOfAnyExcept((byte)pad) < 0)
            return raw.Length - pad;

        var end = raw.Length;
        while (end > 0 && raw[end - 1] == 0)
            end--;
        return end;
    }

    /// <summary>
    /// Import the key <c>/getRSAPublickKey</c> returns: PEM (SubjectPublicKeyInfo or PKCS#1),
    /// or bare Base64 SubjectPublicKeyInfo.
    /// </summary>
    public static RSA ImportPublicKey(string key)
    {
        var rsa = RSA.Create();
        try
        {
            if (key.Contains("-----BEGIN", StringComparison.Ordinal))
                rsa.ImportFromPem(key.Replace("\\n", "\n", StringComparison.Ordinal));
            else
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.Trim()), out _);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }
}

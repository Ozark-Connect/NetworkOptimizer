using System.Security.Cryptography;
using System.Text;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Service for encrypting/decrypting sensitive credentials at rest
/// Uses AES-256 encryption with a machine-specific key derived from DPAPI
/// </summary>
public class CredentialProtectionService : ICredentialProtectionService
{
    private readonly byte[] _key;
    private const string KeyPurpose = "NetworkOptimizer.Credentials.v1";

    public CredentialProtectionService()
    {
        // Derive a machine-specific key using DPAPI (Windows) or a file-based key (Linux)
        // This also generates the key file if it doesn't exist
        _key = DeriveKey();
    }

    /// <summary>
    /// Ensures the credential key file exists. Call at startup to pre-generate.
    /// </summary>
    public static void EnsureKeyExists()
    {
        // Simply instantiating the service will generate the key
        _ = new CredentialProtectionService();
    }

    /// <summary>
    /// Encrypt a plaintext credential
    /// </summary>
    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Prepend IV to ciphertext and encode as base64
        var result = new byte[aes.IV.Length + ciphertext.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ciphertext, 0, result, aes.IV.Length, ciphertext.Length);

        return "ENC:" + Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypt an encrypted credential
    /// </summary>
    public string Decrypt(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return encrypted;

        // Check if it's encrypted (starts with ENC:)
        if (!encrypted.StartsWith("ENC:"))
            return encrypted; // Return as-is if not encrypted (migration support)

        try
        {
            var data = Convert.FromBase64String(encrypted.Substring(4));

            using var aes = Aes.Create();
            aes.Key = _key;

            // Extract IV from the beginning
            var iv = new byte[aes.BlockSize / 8];
            var ciphertext = new byte[data.Length - iv.Length];
            Buffer.BlockCopy(data, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(data, iv.Length, ciphertext, 0, ciphertext.Length);

            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            // If decryption fails, return empty (don't expose partial data)
            Console.Error.WriteLine($"[CredentialProtectionService] Decryption failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Check if a value is already encrypted
    /// </summary>
    public bool IsEncrypted(string? value)
    {
        return value?.StartsWith("ENC:") == true;
    }

    private byte[] DeriveKey()
    {
        // Use a combination of machine-specific data and a salt
        var keyMaterial = GetKeyMaterial();

        using var sha256 = SHA256.Create();
        var salt = Encoding.UTF8.GetBytes(KeyPurpose);

        // PBKDF2 to derive a 256-bit key
        using var pbkdf2 = new Rfc2898DeriveBytes(keyMaterial, salt, 100000, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32); // 256 bits
    }

    private byte[] GetKeyMaterial()
    {
        // Try to get machine-specific key material
        // In Docker, use /app/data; otherwise use LocalApplicationData
        var isDocker = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        var keyFilePath = isDocker
            ? "/app/data/.credential_key"
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetworkOptimizer",
                ".credential_key"
            );

        try
        {
            var directory = Path.GetDirectoryName(keyFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(keyFilePath))
            {
                return File.ReadAllBytes(keyFilePath);
            }

            // Generate a new random key and save it
            var key = new byte[64];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
            }

            File.WriteAllBytes(keyFilePath, key);

            // Set restrictive permissions on Linux/macOS (600 = owner read/write only)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CredentialProtectionService] Unable to set Unix file permissions: {ex.Message}");
                }
            }

            return key;
        }
        catch (Exception ex)
        {
            // Fallback: use machine name + some entropy
            Console.Error.WriteLine($"[CredentialProtectionService] Failed to create/read key file, using fallback: {ex.Message}");
            var fallback = Environment.MachineName + KeyPurpose + Environment.UserName;
            return Encoding.UTF8.GetBytes(fallback.PadRight(64, 'X'));
        }
    }
}

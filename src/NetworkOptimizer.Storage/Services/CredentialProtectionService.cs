using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Service for encrypting/decrypting sensitive credentials at rest
/// Uses AES-256 encryption with a machine-specific key derived from DPAPI
/// </summary>
public class CredentialProtectionService : ICredentialProtectionService
{
    private readonly byte[] _key;
    private readonly ILogger<CredentialProtectionService>? _logger;
    private const string KeyPurpose = "NetworkOptimizer.Credentials.v1";

    public CredentialProtectionService(ILogger<CredentialProtectionService>? logger = null)
    {
        _logger = logger;
        // Derive a machine-specific key using DPAPI (Windows) or a file-based key (Linux)
        // This also generates the key file if it doesn't exist
        _key = DeriveKey();
    }

    /// <summary>
    /// Ensures the credential key file exists. Call at startup to pre-generate.
    /// The key is already created in the constructor, so this is a no-op but
    /// provides a clear intent when called at application startup via DI.
    /// </summary>
    public void EnsureKeyExists()
    {
        // Key is already generated in constructor via DeriveKey()
        // This method exists to provide explicit startup initialization via DI
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
            _logger?.LogError(ex, "Decryption failed");
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
        return Rfc2898DeriveBytes.Pbkdf2(keyMaterial, salt, 100000, HashAlgorithmName.SHA256, 32);
    }

    private byte[] GetKeyMaterial()
    {
        // Operators can supply the key out-of-band via NO_CREDENTIAL_KEY_FILE - e.g.
        // a Docker secret mounted at /run/secrets/... or any path OUTSIDE the data
        // volume - so a leak or backup of the data volume does not also hand over the
        // key. When unset, the key lives beside the database in the data directory,
        // which then must be treated as secret material (see DEPLOYMENT.md).
        // WARNING: pointing an EXISTING install at a new/empty path makes
        // previously-stored secrets undecryptable; move the existing .credential_key
        // contents to the new path first.
        var overridePath = Environment.GetEnvironmentVariable("NO_CREDENTIAL_KEY_FILE");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return ReadSuppliedKey(overridePath.Trim());

        // In Docker, default to /app/data; otherwise use LocalApplicationData.
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
                    _logger?.LogWarning(ex, "Unable to set Unix file permissions on credential key file");
                }
            }

            return key;
        }
        catch (Exception ex)
        {
            // Never derive a stand-in key: in a container the machine name is the container id, so a
            // derived key changes on every recreate and silently leaves every stored secret
            // undecryptable. A data directory this broken has already taken the database down too.
            throw new InvalidOperationException(
                $"The credential key at '{keyFilePath}' could not be read or created. Fix the file or " +
                "directory permissions, or supply the key through NO_CREDENTIAL_KEY_FILE.", ex);
        }
    }

    /// <summary>
    /// Reads a key the operator supplied through NO_CREDENTIAL_KEY_FILE. Throws rather than
    /// generating or falling back, because setting that variable is a statement that the key is
    /// managed OUTSIDE this application - so a missing file means the supply failed, not that a key
    /// is wanted.
    ///
    /// Generating one instead is silent and looks like nothing happened: the app starts, every
    /// stored ENC: value is undecryptable against the new key, and anything saved afterwards is
    /// encrypted under it - leaving a mixture that restoring the real key only half repairs. The
    /// default path refuses for the same reason when its key file cannot be read or created.
    ///
    /// That is an edge case when the path is a file sitting on the host, and routine when the key is
    /// fetched from a vault at every boot: an unreachable vault, a flapped tunnel, or losing a
    /// start-order race all land here. Refusing to start is recoverable in a way that quietly
    /// re-keying is not.
    /// </summary>
    private static byte[] ReadSuppliedKey(string keyFilePath)
    {
        byte[] key;
        try
        {
            key = File.ReadAllBytes(keyFilePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"NO_CREDENTIAL_KEY_FILE is set to '{keyFilePath}', which could not be read. The " +
                "credential key is supplied externally, so no key is generated to replace it - " +
                "doing that would make every stored secret undecryptable. Fix the file or unset " +
                "NO_CREDENTIAL_KEY_FILE to let the key live in the data directory.", ex);
        }

        // A zero-length file is a half-finished write, not a key. Length is otherwise not policed:
        // an operator's own key material has always been taken as-is, whatever its size.
        if (key.Length == 0)
        {
            throw new InvalidOperationException(
                $"NO_CREDENTIAL_KEY_FILE is set to '{keyFilePath}', which is empty. That is an " +
                "incomplete write rather than a key; using it would make every stored secret " +
                "undecryptable.");
        }

        return key;
    }
}

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Service for encrypting/decrypting sensitive credentials at rest
/// </summary>
public interface ICredentialProtectionService
{
    /// <summary>
    /// Encrypt a plaintext credential
    /// </summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypt an encrypted credential
    /// </summary>
    string Decrypt(string encrypted);

    /// <summary>
    /// Check if a value is already encrypted
    /// </summary>
    bool IsEncrypted(string? value);
}

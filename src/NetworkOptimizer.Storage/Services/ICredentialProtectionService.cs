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
    /// Encrypt a credential that belongs to one site, such as a console password or a device login.
    /// One key protects every site here, so the site is ignored; an implementation may key per site.
    /// </summary>
    string EncryptForSite(string plaintext, string siteSlug) => Encrypt(plaintext);

    /// <summary>
    /// Decrypt an encrypted credential
    /// </summary>
    string Decrypt(string encrypted);

    /// <summary>
    /// Check if a value is already encrypted
    /// </summary>
    bool IsEncrypted(string? value);

    /// <summary>
    /// Ensures the credential key file exists. Call at startup to pre-generate.
    /// </summary>
    void EnsureKeyExists();
}

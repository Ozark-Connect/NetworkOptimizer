namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Abstraction for decrypting secrets stored in delivery channel configs.
/// Implemented by the credential protection infrastructure and registered in DI.
/// </summary>
public interface ISecretDecryptor
{
    string Decrypt(string encrypted);
    string Encrypt(string plaintext);

    /// <summary>
    /// Encrypts a secret for a delivery channel that belongs to one site.
    /// </summary>
    string EncryptForSite(string plaintext, string siteSlug) => Encrypt(plaintext);
}

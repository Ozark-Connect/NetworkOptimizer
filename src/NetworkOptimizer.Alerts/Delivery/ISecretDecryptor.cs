namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Abstraction for decrypting secrets stored in delivery channel configs.
/// Implemented by the credential protection infrastructure and registered in DI.
/// </summary>
public interface ISecretDecryptor
{
    string Decrypt(string encrypted);
    string Encrypt(string plaintext);
}

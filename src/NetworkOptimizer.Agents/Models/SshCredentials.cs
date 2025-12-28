namespace NetworkOptimizer.Agents.Models;

/// <summary>
/// SSH connection credentials supporting both password and key-based authentication
/// </summary>
public class SshCredentials
{
    /// <summary>
    /// SSH hostname or IP address
    /// </summary>
    public required string Host { get; set; }

    /// <summary>
    /// SSH port (default: 22)
    /// </summary>
    public int Port { get; set; } = 22;

    /// <summary>
    /// SSH username
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    /// Password for password-based authentication
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Private key path for key-based authentication
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Private key passphrase (if the key is encrypted)
    /// </summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>
    /// Connection timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Validates that credentials are properly configured
    /// </summary>
    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username))
            return false;

        // Must have either password or private key
        return !string.IsNullOrWhiteSpace(Password) || !string.IsNullOrWhiteSpace(PrivateKeyPath);
    }

    /// <summary>
    /// Gets authentication type
    /// </summary>
    public AuthenticationType GetAuthenticationType()
    {
        if (!string.IsNullOrWhiteSpace(PrivateKeyPath))
            return AuthenticationType.PrivateKey;

        if (!string.IsNullOrWhiteSpace(Password))
            return AuthenticationType.Password;

        return AuthenticationType.None;
    }
}

public enum AuthenticationType
{
    None,
    Password,
    PrivateKey
}

using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Unified SSH connection information for use with SshClientService.
/// Created from various settings models via factory methods.
/// </summary>
public class SshConnectionInfo
{
    /// <summary>SSH hostname or IP address</summary>
    public required string Host { get; set; }

    /// <summary>SSH port (default 22)</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH username</summary>
    public required string Username { get; set; }

    /// <summary>Decrypted password for password-based auth</summary>
    public string? Password { get; set; }

    /// <summary>Path to private key file for key-based auth</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Passphrase for encrypted private keys</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Connection timeout</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether credentials are configured</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(PrivateKeyPath);

    /// <summary>Whether to use password auth (vs key-based)</summary>
    public bool UsePasswordAuth => !string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PrivateKeyPath);

    /// <summary>
    /// Create connection info from gateway SSH settings.
    /// </summary>
    /// <param name="settings">Gateway SSH settings from database</param>
    /// <param name="decryptedPassword">Decrypted password (null if using key auth)</param>
    public static SshConnectionInfo FromGatewaySettings(GatewaySshSettings settings, string? decryptedPassword)
    {
        return new SshConnectionInfo
        {
            Host = settings.Host ?? throw new InvalidOperationException("Gateway host not configured"),
            Port = settings.Port,
            Username = settings.Username,
            Password = decryptedPassword,
            PrivateKeyPath = settings.PrivateKeyPath,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Create connection info from UniFi SSH settings for a specific host.
    /// </summary>
    /// <param name="settings">Global UniFi SSH settings</param>
    /// <param name="host">Target host IP or hostname</param>
    /// <param name="decryptedPassword">Decrypted password (null if using key auth)</param>
    public static SshConnectionInfo FromUniFiSettings(UniFiSshSettings settings, string host, string? decryptedPassword)
    {
        return new SshConnectionInfo
        {
            Host = host,
            Port = settings.Port,
            Username = settings.Username,
            Password = decryptedPassword,
            PrivateKeyPath = settings.PrivateKeyPath,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Create connection info from UniFi settings with device-specific overrides.
    /// Device settings take precedence over global settings.
    /// </summary>
    /// <param name="globalSettings">Global UniFi SSH settings</param>
    /// <param name="device">Device with optional credential overrides</param>
    /// <param name="decryptedGlobalPassword">Decrypted global password</param>
    /// <param name="decryptedDevicePassword">Decrypted device-specific password</param>
    public static SshConnectionInfo FromDeviceWithOverrides(
        UniFiSshSettings globalSettings,
        DeviceSshConfiguration device,
        string? decryptedGlobalPassword,
        string? decryptedDevicePassword)
    {
        // Device-specific credentials take precedence
        var username = !string.IsNullOrEmpty(device.SshUsername) ? device.SshUsername : globalSettings.Username;
        var password = !string.IsNullOrEmpty(decryptedDevicePassword) ? decryptedDevicePassword : decryptedGlobalPassword;
        var keyPath = !string.IsNullOrEmpty(device.SshPrivateKeyPath) ? device.SshPrivateKeyPath : globalSettings.PrivateKeyPath;

        return new SshConnectionInfo
        {
            Host = device.Host,
            Port = globalSettings.Port,
            Username = username,
            Password = password,
            PrivateKeyPath = keyPath,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}

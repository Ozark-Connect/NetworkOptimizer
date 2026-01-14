using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Service for SSH operations on the UniFi gateway/UDM.
/// The gateway typically has different SSH credentials than other UniFi devices.
/// Used by GatewaySpeedTestService and SqmDeploymentService.
/// </summary>
public interface IGatewaySshService
{
    /// <summary>
    /// Get the gateway SSH settings (creates default if none exist)
    /// </summary>
    /// <param name="forceRefresh">If true, bypasses cache and loads fresh from database</param>
    Task<GatewaySshSettings> GetSettingsAsync(bool forceRefresh = false);

    /// <summary>
    /// Save gateway SSH settings
    /// </summary>
    Task<GatewaySshSettings> SaveSettingsAsync(GatewaySshSettings settings);

    /// <summary>
    /// Test SSH connection to the gateway using saved settings
    /// </summary>
    Task<(bool success, string message)> TestConnectionAsync();

    /// <summary>
    /// Test SSH connection to the gateway using provided settings (for testing form values before save)
    /// </summary>
    /// <param name="host">Gateway hostname or IP</param>
    /// <param name="port">SSH port</param>
    /// <param name="username">SSH username</param>
    /// <param name="password">Plain text password (not encrypted)</param>
    /// <param name="privateKeyPath">Path to private key file</param>
    Task<(bool success, string message)> TestConnectionAsync(
        string host,
        int port,
        string username,
        string? password,
        string? privateKeyPath);

    /// <summary>
    /// Run an SSH command on the gateway
    /// </summary>
    /// <param name="command">Command to execute</param>
    /// <param name="timeout">Optional command timeout (default 30 seconds)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<(bool success, string output)> RunCommandAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

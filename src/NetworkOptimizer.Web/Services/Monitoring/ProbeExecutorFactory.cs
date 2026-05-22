using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Resolves the appropriate <see cref="IProbeExecutor"/> for a chosen vantage point. The
/// server vantage is the singleton <see cref="LocalProbeExecutor"/>; device vantages are
/// constructed on demand from the device's IP + the user's UniFi SSH credentials.
///
/// Device executors aren't cached because the credentials, the device IP, and the
/// connectivity to it can all change between requests, and a stale cached executor would
/// hide that. Construction is cheap; it's just an object that holds an
/// <see cref="SshConnectionInfo"/>.
/// </summary>
public class ProbeExecutorFactory
{
    private readonly LocalProbeExecutor _local;
    private readonly UniFiSshService _uniFiSsh;
    private readonly SshClientService _sshClient;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly UniFiConnectionService _connection;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProbeExecutorFactory> _logger;

    public ProbeExecutorFactory(
        LocalProbeExecutor local,
        UniFiSshService uniFiSsh,
        SshClientService sshClient,
        ICredentialProtectionService credentialProtection,
        UniFiConnectionService connection,
        ILoggerFactory loggerFactory,
        ILogger<ProbeExecutorFactory> logger)
    {
        _local = local;
        _uniFiSsh = uniFiSsh;
        _sshClient = sshClient;
        _credentialProtection = credentialProtection;
        _connection = connection;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public IProbeExecutor GetServer() => _local;

    /// <summary>
    /// Build an executor that runs probes from the chosen UniFi device via SSH. Returns
    /// null if SSH isn't configured for this device or its IP can't be resolved from the
    /// current topology.
    /// </summary>
    public async Task<IProbeExecutor?> ForDeviceAsync(string deviceMac, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(deviceMac)) return null;

        try
        {
            // Look up the device's current management IP from topology.
            if (!_connection.IsConnected) return null;
            var devices = await _connection.GetDiscoveredDevicesAsync(ct);
            var device = devices?.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.Mac) &&
                string.Equals(d.Mac.Replace('-', ':').ToLowerInvariant(),
                              deviceMac.Replace('-', ':').ToLowerInvariant()));
            if (device == null || string.IsNullOrEmpty(device.DisplayIpAddress)) return null;

            // Pull the user's UniFi SSH creds; require both username and a credential
            // (password OR key path) before we attempt to construct anything.
            var sshSettings = await _uniFiSsh.GetSettingsAsync();
            if (sshSettings == null
                || string.IsNullOrEmpty(sshSettings.Username)
                || (string.IsNullOrEmpty(sshSettings.Password) && string.IsNullOrEmpty(sshSettings.PrivateKeyPath)))
            {
                _logger.LogDebug("UniFi SSH credentials not configured; cannot build device vantage for {Mac}", deviceMac);
                return null;
            }

            string? decryptedPassword = null;
            if (!string.IsNullOrEmpty(sshSettings.Password))
            {
                decryptedPassword = _credentialProtection.Decrypt(sshSettings.Password);
            }

            var connection = SshConnectionInfo.FromUniFiSettings(sshSettings, device.DisplayIpAddress, decryptedPassword);
            return new SshProbeExecutor(
                _sshClient,
                connection,
                vantageId: $"device:{deviceMac}",
                _loggerFactory.CreateLogger<SshProbeExecutor>());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to construct SSH probe executor for {Mac}", deviceMac);
            return null;
        }
    }
}

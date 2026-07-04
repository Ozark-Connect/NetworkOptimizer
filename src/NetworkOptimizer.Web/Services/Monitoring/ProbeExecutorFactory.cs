using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Services;
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
    private readonly IGatewaySshService _gatewaySsh;
    private readonly SshClientService _sshClient;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly UniFiConnectionService _connection;
    private readonly AgentProbeService _agentProbe;
    private readonly SiteContextService _siteContext;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProbeExecutorFactory> _logger;

    public ProbeExecutorFactory(
        LocalProbeExecutor local,
        UniFiSshService uniFiSsh,
        IGatewaySshService gatewaySsh,
        SshClientService sshClient,
        ICredentialProtectionService credentialProtection,
        UniFiConnectionService connection,
        AgentProbeService agentProbe,
        SiteContextService siteContext,
        ILoggerFactory loggerFactory,
        ILogger<ProbeExecutorFactory> logger)
    {
        _local = local;
        _uniFiSsh = uniFiSsh;
        _gatewaySsh = gatewaySsh;
        _sshClient = sshClient;
        _credentialProtection = credentialProtection;
        _connection = connection;
        _agentProbe = agentProbe;
        _siteContext = siteContext;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// The "server" vantage. On a secondary site with an online agent that's the on-site
    /// agent host (the central server can't reach the site's network) - it runs the
    /// identical LocalProbeExecutor over the tunnel. Falls back to the local server on the
    /// default site or when no agent is online.
    /// </summary>
    public IProbeExecutor GetServer()
    {
        if (!_siteContext.IsDefault && _agentProbe.HasAgentForSite(_siteContext.Slug))
            return new AgentProbeExecutor(_agentProbe, _siteContext.Slug,
                _loggerFactory.CreateLogger<AgentProbeExecutor>());
        return _local;
    }

    /// <summary>Whether the "server" vantage resolves to the on-site agent for the current site.</summary>
    public bool ServerVantageIsAgent =>
        !_siteContext.IsDefault && _agentProbe.HasAgentForSite(_siteContext.Slug);

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
            if (!_connection.IsConnected) return null;
            var devices = await _connection.GetDiscoveredDevicesAsync(ct);
            var device = devices?.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.Mac) &&
                string.Equals(d.Mac.Replace('-', ':').ToLowerInvariant(),
                              deviceMac.Replace('-', ':').ToLowerInvariant()));
            if (device == null || string.IsNullOrEmpty(device.DisplayIpAddress)) return null;

            SshConnectionInfo connection;

            if (device.Type == DeviceType.Gateway)
            {
                var gwSettings = await _gatewaySsh.GetSettingsAsync();
                if (string.IsNullOrEmpty(gwSettings.Username)
                    || (string.IsNullOrEmpty(gwSettings.Password) && string.IsNullOrEmpty(gwSettings.PrivateKeyPath)))
                {
                    _logger.LogDebug("Gateway SSH credentials not configured; cannot build gateway vantage for {Mac}", deviceMac);
                    return null;
                }

                string? decryptedPassword = null;
                if (!string.IsNullOrEmpty(gwSettings.Password))
                    decryptedPassword = _credentialProtection.Decrypt(gwSettings.Password);

                connection = SshConnectionInfo.FromGatewaySettings(gwSettings, decryptedPassword);
            }
            else
            {
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
                    decryptedPassword = _credentialProtection.Decrypt(sshSettings.Password);

                connection = SshConnectionInfo.FromUniFiSettings(sshSettings, device.DisplayIpAddress, decryptedPassword);
            }

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

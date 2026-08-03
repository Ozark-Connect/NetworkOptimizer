using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Service for SSH operations on the UniFi gateway/UDM.
/// Uses SSH.NET via SshClientService for cross-platform support.
/// One instance exists per site, owned by <see cref="GatewaySshRegistry"/>: settings
/// come from that site's database and the gateway-host fallback from that site's
/// console connection, so commands land on the right gateway.
/// </summary>
public class GatewaySshService : IGatewaySshService
{
    private readonly ILogger<GatewaySshService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly SshClientService _sshClient;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly UniFiConnectionService _connectionService;
    private readonly string _siteSlug;

    // Cache the settings to avoid repeated DB queries
    private GatewaySshSettings? _cachedSettings;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Shown when the gateway is reached through this site's on-site agent, which
    /// isn't online yet - dialing the loopback tunnel proxy now would just get a
    /// closed socket that SSH.NET reports as a raw "no identification string"
    /// protocol error. Mirrors the console's awaiting-agent message.
    /// </summary>
    public const string AwaitingAgentMessage =
        "Waiting for the on-site agent to connect. This site's gateway is reached through its agent, and will connect automatically once the agent is online.";

    public GatewaySshService(
        ILogger<GatewaySshService> logger,
        IServiceProvider serviceProvider,
        SshClientService sshClient,
        ICredentialProtectionService credentialProtection,
        SiteConnectionRegistry siteConnections,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshClient = sshClient;
        _credentialProtection = credentialProtection;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _connectionService = siteConnections.GetFor(_siteSlug);
    }

    /// <summary>
    /// Creates a DI scope pinned to this instance's site so scoped services
    /// (repositories, DbContext) hit this site's database.
    /// </summary>
    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }

    /// <summary>
    /// Routes a connection through the site's agent tunnel when the site's devices
    /// are reached via agent: SSH.NET then dials a loopback proxy port that the
    /// agent forwards to the real host inside the site's network.
    /// </summary>
    /// <remarks>
    /// The proxied SSH is end-to-end between SSH.NET and the real gateway (the agent
    /// only pipes bytes), but we intentionally do NOT pin the gateway's SSH host key.
    /// Hard pinning is impractical: UniFi regenerates host keys on firmware upgrades
    /// (and adoption/factory reset), so a strict pin would break SSH after routine
    /// updates and train operators to click through warnings. The residual risk -
    /// a rogue agent or leaked agentKey presenting a fake gateway to harvest
    /// credentials - is addressed at the tunnel instead (guard the agentKey,
    /// IP-allowlist the tunnel endpoint to site IPs, and the planned one-tunnel-
    /// per-key + source-IP alert), not by host-key pinning here. See the agent
    /// README "Security and hardening" section and TODO.md.
    /// </remarks>
    private async Task<SshConnectionInfo> MaybeRouteViaAgentAsync(SshConnectionInfo connection)
    {
        // Every connection this service builds passes through here, so it is also where the site's
        // stored key gets attached - additive to whatever password or key file is already set.
        using (var scope = CreateSiteScope())
        {
            await StoredSshKeyReader.AttachAsync(scope.ServiceProvider, connection);
        }

        var routing = _serviceProvider.GetService<SiteTunnelRouting>();
        if (routing == null) return connection;
        (connection.Host, connection.Port) = await routing.RouteAsync(_siteSlug, connection.Host, connection.Port);
        return connection;
    }

    /// <summary>
    /// True when this site's gateway is reached through its on-site agent but no
    /// agent is currently online. Callers short-circuit on this so they report a
    /// clear "waiting for the agent" state (see <see cref="AwaitingAgentMessage"/>)
    /// instead of dialing the refusing loopback proxy - which surfaces a bogus SSH
    /// protocol error to the user and logs a warning for a transient startup state.
    /// </summary>
    private async Task<bool> IsAwaitingAgentAsync()
    {
        var routing = _serviceProvider.GetService<SiteTunnelRouting>();
        if (routing == null) return false;
        return await routing.IsViaAgentAsync(_siteSlug) && !routing.IsAgentOnline(_siteSlug);
    }

    /// <inheritdoc />
    public Task<bool> IsAwaitingAgentTunnelAsync() => IsAwaitingAgentAsync();

    /// <inheritdoc />
    public async Task<GatewaySshSettings> GetSettingsAsync(bool forceRefresh = false)
    {
        // Check cache first (unless force refresh requested)
        if (!forceRefresh && _cachedSettings != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _cachedSettings;
        }

        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        var settings = await repository.GetGatewaySshSettingsAsync();

        if (settings == null)
        {
            // Create default settings, try to get gateway host from controller
            var gatewayHost = GetGatewayHostFromController();

            settings = new GatewaySshSettings
            {
                Host = gatewayHost,
                Username = "root",
                Port = 22,
                Iperf3Port = 5201,
                Enabled = true,  // Default to enabled for new installs
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveGatewaySshSettingsAsync(settings);
        }

        // Set here so HasCredentials is correct for every caller, rather than each of the two dozen
        // places that check it having to learn about stored keys.
        settings.HasStoredKey = await StoredSshKeyReader.ExistsAsync(scope.ServiceProvider);

        _cachedSettings = settings;
        _cacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <inheritdoc />
    public async Task<GatewaySshSettings> SaveSettingsAsync(GatewaySshSettings settings)
    {
        using var scope = CreateSiteScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        settings.UpdatedAt = DateTime.UtcNow;

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(settings.Password) && !_credentialProtection.IsEncrypted(settings.Password))
        {
            settings.Password = _credentialProtection.Encrypt(settings.Password);
        }

        await repository.SaveGatewaySshSettingsAsync(settings);

        // The caller uses the object we hand back, so it needs the same stored-key flag a load sets -
        // otherwise HasCredentials reads false right after saving on a stored-key-only site.
        settings.HasStoredKey = await StoredSshKeyReader.ExistsAsync(scope.ServiceProvider);

        // Invalidate cache
        _cachedSettings = null;

        return settings;
    }

    /// <inheritdoc />
    public async Task<(bool success, string message)> TestConnectionAsync()
    {
        var settings = await GetSettingsAsync();

        if (!settings.Enabled)
        {
            return (false, "Gateway SSH access is disabled");
        }

        if (string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway host not configured");
        }

        if (!settings.HasCredentials)
        {
            return (false, "SSH credentials not configured");
        }

        if (await IsAwaitingAgentAsync())
        {
            return (false, AwaitingAgentMessage);
        }

        try
        {
            var connection = await CreateConnectionInfoAsync(settings);
            var (success, message) = await _sshClient.TestConnectionAsync(connection);

            if (success)
            {
                // Verify with a simple command
                var result = await _sshClient.ExecuteCommandAsync(connection, "echo Connection_OK");
                if (result.Success && result.Output.Contains("Connection_OK"))
                {
                    // Update last tested
                    settings.LastTestedAt = DateTime.UtcNow;
                    settings.LastTestResult = "Success";
                    await SaveSettingsAsync(settings);

                    return (true, "SSH connection successful");
                }
                return (false, result.Error ?? "Connection test command failed");
            }

            return (false, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway SSH connection test failed for {Host}", settings.Host);
            return (false, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<(bool success, string message)> TestConnectionAsync(
        string host,
        int port,
        string username,
        string? password,
        string? privateKeyPath)
    {
        if (string.IsNullOrEmpty(host))
        {
            return (false, "Gateway host not configured");
        }

        if (string.IsNullOrEmpty(password) && string.IsNullOrEmpty(privateKeyPath))
        {
            return (false, "SSH credentials not configured");
        }

        if (await IsAwaitingAgentAsync())
        {
            return (false, AwaitingAgentMessage);
        }

        try
        {
            var connection = await MaybeRouteViaAgentAsync(new SshConnectionInfo
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                PrivateKeyPath = privateKeyPath,
                Timeout = TimeSpan.FromSeconds(5)
            });

            var (success, message) = await _sshClient.TestConnectionAsync(connection);

            if (success)
            {
                // Verify with a simple command
                var result = await _sshClient.ExecuteCommandAsync(connection, "echo Connection_OK");
                if (result.Success && result.Output.Contains("Connection_OK"))
                {
                    return (true, "SSH connection successful");
                }
                return (false, result.Error ?? "Connection test command failed");
            }

            return (false, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway SSH connection test failed for {Host}", host);
            return (false, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<(bool success, string output)> RunCommandAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync();

        if (!settings.Enabled)
        {
            return (false, "Gateway SSH access is disabled");
        }

        if (string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway host not configured");
        }

        if (!settings.HasCredentials)
        {
            return (false, "SSH credentials not configured");
        }

        if (await IsAwaitingAgentAsync())
        {
            return (false, AwaitingAgentMessage);
        }

        var connection = await CreateConnectionInfoAsync(settings);
        var result = await _sshClient.ExecuteCommandAsync(connection, command, timeout, cancellationToken);

        return (result.Success, result.CombinedOutput);
    }

    /// <inheritdoc />
    public async Task<SshConnectionInfo?> GetConnectionInfoAsync()
    {
        var settings = await GetSettingsAsync();
        if (!settings.Enabled || string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
            return null;
        if (await IsAwaitingAgentAsync())
            return null;
        return await CreateConnectionInfoAsync(settings);
    }

    /// <summary>
    /// Create SshConnectionInfo from gateway settings with decrypted password,
    /// routed through the site's agent tunnel when configured.
    /// </summary>
    private async Task<SshConnectionInfo> CreateConnectionInfoAsync(GatewaySshSettings settings)
    {
        string? decryptedPassword = null;
        if (!string.IsNullOrEmpty(settings.Password))
        {
            decryptedPassword = _credentialProtection.Decrypt(settings.Password);
        }

        return await MaybeRouteViaAgentAsync(SshConnectionInfo.FromGatewaySettings(settings, decryptedPassword));
    }

    /// <summary>
    /// Try to get gateway host from controller URL.
    /// </summary>
    private string? GetGatewayHostFromController()
    {
        if (_connectionService.CurrentConfig != null)
        {
            try
            {
                var uri = new Uri(_connectionService.CurrentConfig.ControllerUrl);
                return uri.Host;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}

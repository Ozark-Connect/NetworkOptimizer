using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Service for SSH operations on the UniFi gateway/UDM.
/// Uses SSH.NET via SshClientService for cross-platform support.
/// </summary>
public class GatewaySshService : IGatewaySshService
{
    private readonly ILogger<GatewaySshService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly SshClientService _sshClient;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly UniFiConnectionService _connectionService;

    // Cache the settings per site to avoid repeated DB queries
    private readonly ConcurrentDictionary<int, (GatewaySshSettings settings, DateTime cacheTime)> _settingsCache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public GatewaySshService(
        ILogger<GatewaySshService> logger,
        IServiceProvider serviceProvider,
        SshClientService sshClient,
        ICredentialProtectionService credentialProtection,
        UniFiConnectionService connectionService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshClient = sshClient;
        _credentialProtection = credentialProtection;
        _connectionService = connectionService;
    }

    /// <inheritdoc />
    public async Task<GatewaySshSettings> GetSettingsAsync(int siteId, bool forceRefresh = false)
    {
        // Check cache first (unless force refresh requested)
        if (!forceRefresh && _settingsCache.TryGetValue(siteId, out var cached) && DateTime.UtcNow - cached.cacheTime < _cacheExpiry)
        {
            return cached.settings;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        var settings = await repository.GetGatewaySshSettingsAsync(siteId);

        if (settings == null)
        {
            // Create default settings, try to get gateway host from controller
            var gatewayHost = GetGatewayHostFromController(siteId);

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
            await repository.SaveGatewaySshSettingsAsync(siteId, settings);
        }

        _settingsCache[siteId] = (settings, DateTime.UtcNow);

        return settings;
    }

    /// <inheritdoc />
    public async Task<GatewaySshSettings> SaveSettingsAsync(int siteId, GatewaySshSettings settings)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        settings.UpdatedAt = DateTime.UtcNow;

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(settings.Password) && !_credentialProtection.IsEncrypted(settings.Password))
        {
            settings.Password = _credentialProtection.Encrypt(settings.Password);
        }

        await repository.SaveGatewaySshSettingsAsync(siteId, settings);

        // Invalidate cache for this site
        _settingsCache.TryRemove(siteId, out _);

        return settings;
    }

    /// <inheritdoc />
    public async Task<(bool success, string message)> TestConnectionAsync(int siteId)
    {
        var settings = await GetSettingsAsync(siteId);

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

        try
        {
            var connection = CreateConnectionInfo(settings);
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
                    await SaveSettingsAsync(siteId, settings);

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
        int siteId,
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

        try
        {
            var connection = new SshConnectionInfo
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                PrivateKeyPath = privateKeyPath,
                Timeout = TimeSpan.FromSeconds(30)
            };

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
        int siteId,
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(siteId);

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

        var connection = CreateConnectionInfo(settings);
        var result = await _sshClient.ExecuteCommandAsync(connection, command, timeout, cancellationToken);

        return (result.Success, result.Success ? result.Output : result.CombinedOutput);
    }

    /// <summary>
    /// Create SshConnectionInfo from gateway settings with decrypted password.
    /// </summary>
    private SshConnectionInfo CreateConnectionInfo(GatewaySshSettings settings)
    {
        string? decryptedPassword = null;
        if (!string.IsNullOrEmpty(settings.Password))
        {
            decryptedPassword = _credentialProtection.Decrypt(settings.Password);
        }

        return SshConnectionInfo.FromGatewaySettings(settings, decryptedPassword);
    }

    /// <summary>
    /// Try to get gateway host from controller URL.
    /// </summary>
    private string? GetGatewayHostFromController(int siteId)
    {
        var config = _connectionService.GetCurrentConfig(siteId);
        if (config != null)
        {
            try
            {
                var uri = new Uri(config.ControllerUrl);
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

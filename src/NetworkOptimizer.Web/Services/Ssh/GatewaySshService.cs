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

    // Cache the settings to avoid repeated DB queries
    private GatewaySshSettings? _cachedSettings;
    private DateTime _cacheTime = DateTime.MinValue;
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
    public async Task<GatewaySshSettings> GetSettingsAsync(bool forceRefresh = false)
    {
        // Check cache first (unless force refresh requested)
        if (!forceRefresh && _cachedSettings != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _cachedSettings;
        }

        using var scope = _serviceProvider.CreateScope();
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
                Enabled = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveGatewaySshSettingsAsync(settings);
        }

        _cachedSettings = settings;
        _cacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <inheritdoc />
    public async Task<GatewaySshSettings> SaveSettingsAsync(GatewaySshSettings settings)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        settings.UpdatedAt = DateTime.UtcNow;

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(settings.Password) && !_credentialProtection.IsEncrypted(settings.Password))
        {
            settings.Password = _credentialProtection.Encrypt(settings.Password);
        }

        await repository.SaveGatewaySshSettingsAsync(settings);

        // Invalidate cache
        _cachedSettings = null;

        return settings;
    }

    /// <inheritdoc />
    public async Task<(bool success, string message)> TestConnectionAsync()
    {
        var settings = await GetSettingsAsync();

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
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync();

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

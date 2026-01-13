using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing shared SSH credentials and executing SSH commands on UniFi devices.
/// All UniFi network devices (APs, switches) share the same SSH credentials.
/// Uses SSH.NET via SshClientService for cross-platform support.
/// </summary>
public class UniFiSshService : IUniFiSshService
{
    private readonly ILogger<UniFiSshService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly SshClientService _sshClient;

    // Cache the settings to avoid repeated DB queries
    private UniFiSshSettings? _cachedSettings;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public UniFiSshService(
        ILogger<UniFiSshService> logger,
        IServiceProvider serviceProvider,
        ICredentialProtectionService credentialProtection,
        SshClientService sshClient)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _credentialProtection = credentialProtection;
        _sshClient = sshClient;
    }

    /// <summary>
    /// Get the shared SSH settings (creates default if none exist)
    /// </summary>
    public async Task<UniFiSshSettings> GetSettingsAsync()
    {
        // Check cache first
        if (_cachedSettings != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _cachedSettings;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiSshSettingsAsync();

        if (settings == null)
        {
            // Create default settings
            settings = new UniFiSshSettings
            {
                Username = "",
                Port = 22,
                Enabled = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveUniFiSshSettingsAsync(settings);
        }

        _cachedSettings = settings;
        _cacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <summary>
    /// Save SSH settings
    /// </summary>
    public async Task<UniFiSshSettings> SaveSettingsAsync(UniFiSshSettings settings)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        settings.UpdatedAt = DateTime.UtcNow;

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(settings.Password) && !_credentialProtection.IsEncrypted(settings.Password))
        {
            settings.Password = _credentialProtection.Encrypt(settings.Password);
        }

        await repository.SaveUniFiSshSettingsAsync(settings);

        // Invalidate cache
        _cachedSettings = null;

        return settings;
    }

    /// <summary>
    /// Test SSH connection to a specific host using shared credentials
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(string host)
    {
        var settings = await GetSettingsAsync();

        if (!settings.HasCredentials)
        {
            return (false, "SSH credentials not configured");
        }

        try
        {
            // Use echo without quotes for cross-platform compatibility (Windows/Linux)
            var result = await RunCommandAsync(host, "echo Connection_OK", settings.Port);
            if (result.success && result.output.Contains("Connection_OK"))
            {
                // Update last tested
                settings.LastTestedAt = DateTime.UtcNow;
                settings.LastTestResult = "Success";
                await SaveSettingsAsync(settings);

                return (true, "SSH connection successful");
            }
            return (false, result.output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connection test failed for {Host}", host);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Run an SSH command on a device using shared credentials
    /// </summary>
    public async Task<(bool success, string output)> RunCommandAsync(string host, string command, int? portOverride = null, CancellationToken cancellationToken = default)
    {
        return await RunCommandAsync(host, command, portOverride, null, null, null, cancellationToken);
    }

    /// <summary>
    /// Run an SSH command on a device with optional per-device credential overrides.
    /// If override values are null/empty, falls back to global settings.
    /// </summary>
    public async Task<(bool success, string output)> RunCommandAsync(
        string host,
        string command,
        int? portOverride,
        string? usernameOverride,
        string? passwordOverride,
        string? privateKeyPathOverride,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync();

        // Determine effective credentials (per-device overrides take precedence)
        var effectiveUsername = !string.IsNullOrEmpty(usernameOverride) ? usernameOverride : settings.Username;
        var effectivePassword = !string.IsNullOrEmpty(passwordOverride) ? passwordOverride : settings.Password;
        var effectivePrivateKeyPath = !string.IsNullOrEmpty(privateKeyPathOverride) ? privateKeyPathOverride : settings.PrivateKeyPath;

        // Check if we have any credentials at all
        var hasCredentials = !string.IsNullOrEmpty(effectiveUsername) &&
            (!string.IsNullOrEmpty(effectivePassword) || !string.IsNullOrEmpty(effectivePrivateKeyPath));

        if (!hasCredentials)
        {
            return (false, "SSH credentials not configured");
        }

        var port = portOverride ?? settings.Port;

        // Decrypt password if using password auth
        string? decryptedPassword = null;
        if (!string.IsNullOrEmpty(effectivePassword))
        {
            decryptedPassword = _credentialProtection.Decrypt(effectivePassword);
        }

        // Build connection info
        var connection = new SshConnectionInfo
        {
            Host = host,
            Port = port,
            Username = effectiveUsername,
            Password = decryptedPassword,
            PrivateKeyPath = effectivePrivateKeyPath,
            Timeout = TimeSpan.FromSeconds(30)
        };

        var result = await _sshClient.ExecuteCommandAsync(connection, command, TimeSpan.FromSeconds(30), cancellationToken);

        return (result.Success, result.Success ? result.Output : result.CombinedOutput);
    }

    /// <summary>
    /// Run an SSH command using device-specific credentials if configured, falling back to global settings.
    /// </summary>
    public async Task<(bool success, string output)> RunCommandWithDeviceAsync(DeviceSshConfiguration device, string command, CancellationToken cancellationToken = default)
    {
        return await RunCommandAsync(
            device.Host,
            command,
            null,
            device.SshUsername,
            device.SshPassword,
            device.SshPrivateKeyPath,
            cancellationToken);
    }

    /// <summary>
    /// Test SSH connection to a device using device-specific credentials if configured
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(DeviceSshConfiguration device)
    {
        try
        {
            var result = await RunCommandWithDeviceAsync(device, "echo Connection_OK");
            if (result.success && result.output.Contains("Connection_OK"))
            {
                return (true, "SSH connection successful");
            }
            return (false, result.output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connection test failed for device {Host}", device.Host);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Check if a tool (like iperf3) is available on a device (using global credentials)
    /// </summary>
    public async Task<(bool available, string version)> CheckToolAvailableAsync(string host, string toolName)
    {
        try
        {
            // Run without piping (head -1 is Linux-only) - works on both Windows and Linux
            var result = await RunCommandAsync(host, $"{toolName} --version");
            _logger.LogDebug("CheckToolAvailable({Host}, {Tool}): success={Success}, output={Output}",
                host, toolName, result.success, result.output);
            // Check for tool name without version number (iperf3 outputs "iperf 3.x" not "iperf3")
            var checkName = toolName.Replace("3", "").Replace("2", ""); // "iperf3" -> "iperf"
            if (result.success && result.output.ToLower().Contains(checkName.ToLower()))
            {
                // Get just the first line of output
                var firstLine = result.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return (true, firstLine?.Trim() ?? result.output.Trim());
            }
            return (false, $"{toolName} not found on device");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CheckToolAvailable({Host}, {Tool}) exception", host, toolName);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Check if a tool (like iperf3) is available on a device (using device-specific credentials if configured)
    /// </summary>
    public async Task<(bool available, string version)> CheckToolAvailableAsync(DeviceSshConfiguration device, string toolName)
    {
        try
        {
            var result = await RunCommandWithDeviceAsync(device, $"{toolName} --version");
            _logger.LogDebug("CheckToolAvailable({Host}, {Tool}) with device creds: success={Success}, output={Output}",
                device.Host, toolName, result.success, result.output);
            // Extract just the filename if a path was provided (e.g., /usr/local/bin/iperf3 -> iperf3)
            var baseName = Path.GetFileName(toolName);
            var checkName = baseName.Replace("3", "").Replace("2", "");
            if (result.success && result.output.ToLower().Contains(checkName.ToLower()))
            {
                var firstLine = result.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return (true, firstLine?.Trim() ?? result.output.Trim());
            }
            return (false, $"{toolName} not found on device");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CheckToolAvailable({Host}, {Tool}) with device creds exception", device.Host, toolName);
            return (false, ex.Message);
        }
    }

    #region Device Management

    /// <summary>
    /// Get all configured devices
    /// </summary>
    public async Task<List<DeviceSshConfiguration>> GetDevicesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();
        return await repository.GetDeviceSshConfigurationsAsync();
    }

    /// <summary>
    /// Save a device configuration
    /// </summary>
    public async Task<DeviceSshConfiguration> SaveDeviceAsync(DeviceSshConfiguration device)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(device.SshPassword) && !_credentialProtection.IsEncrypted(device.SshPassword))
        {
            device.SshPassword = _credentialProtection.Encrypt(device.SshPassword);
        }

        await repository.SaveDeviceSshConfigurationAsync(device);
        return device;
    }

    /// <summary>
    /// Delete a device configuration
    /// </summary>
    public async Task DeleteDeviceAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();
        await repository.DeleteDeviceSshConfigurationAsync(id);
    }

    #endregion
}

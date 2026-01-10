using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing shared SSH credentials and executing SSH commands on UniFi devices.
/// All UniFi network devices (APs, switches) share the same SSH credentials.
/// </summary>
public class UniFiSshService : IUniFiSshService
{
    private readonly ILogger<UniFiSshService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICredentialProtectionService _credentialProtection;

    // Cache the settings to avoid repeated DB queries
    private UniFiSshSettings? _cachedSettings;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public UniFiSshService(ILogger<UniFiSshService> logger, IServiceProvider serviceProvider, ICredentialProtectionService credentialProtection)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _credentialProtection = credentialProtection;
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
        var usePassword = !string.IsNullOrEmpty(effectivePassword) && string.IsNullOrEmpty(effectivePrivateKeyPath);

        var sshArgs = new List<string>
        {
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "ConnectTimeout=5"
        };

        // BatchMode=yes disables password prompts, only use with key auth
        if (!usePassword)
        {
            sshArgs.Add("-o");
            sshArgs.Add("BatchMode=yes");
        }

        sshArgs.Add("-p");
        sshArgs.Add(port.ToString());

        // Add key authentication
        if (!string.IsNullOrEmpty(effectivePrivateKeyPath))
        {
            sshArgs.Add("-i");
            sshArgs.Add(effectivePrivateKeyPath);
        }

        sshArgs.Add($"{effectiveUsername}@{host}");
        sshArgs.Add(command);

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // If password auth, use sshpass with environment variable
        if (usePassword)
        {
            var decryptedPassword = _credentialProtection.Decrypt(effectivePassword!);
            startInfo.FileName = "sshpass";
            startInfo.Arguments = $"-e ssh {string.Join(" ", sshArgs)}";
            startInfo.Environment["SSHPASS"] = decryptedPassword;
        }
        else
        {
            startInfo.FileName = "ssh";
            startInfo.Arguments = string.Join(" ", sshArgs);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            // Create a linked token that cancels on timeout OR external cancellation
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred (not external cancellation)
                process.Kill();
                return (false, "SSH command timed out");
            }

            // If we get here due to external cancellation, propagate it
            cancellationToken.ThrowIfCancellationRequested();

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                return (false, string.IsNullOrEmpty(error) ? output : error);
            }

            return (true, output);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH command execution failed for {Host}", host);
            return (false, ex.Message);
        }
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

using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages the UniFi controller connection and configuration persistence.
/// This is a singleton service that maintains the API client across the application.
/// Configuration is stored in the database with encrypted credentials.
/// </summary>
public class UniFiConnectionService : IUniFiClientProvider, IDisposable
{
    private readonly ILogger<UniFiConnectionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICredentialProtectionService _credentialProtection;

    private UniFiApiClient? _client;
    private UniFiConnectionSettings? _settings;
    private bool _isConnected;
    private string? _lastError;
    private DateTime? _lastConnectedAt;

    // Cache to avoid repeated DB queries
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    // Device discovery cache (30 second TTL for dashboard responsiveness)
    private List<DiscoveredDevice>? _cachedDevices;
    private DateTime _deviceCacheTime = DateTime.MinValue;
    private static readonly TimeSpan DeviceCacheDuration = TimeSpan.FromSeconds(30);

    // Lazy initialization for async config loading
    private Task? _initializationTask;
    private readonly object _initLock = new();

    public UniFiConnectionService(ILogger<UniFiConnectionService> logger, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, ICredentialProtectionService credentialProtection)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _credentialProtection = credentialProtection;

        // Start initialization in background (non-blocking)
        StartInitializationAsync();
    }

    /// <summary>
    /// Starts the async initialization without blocking the constructor.
    /// Uses double-checked locking to ensure initialization runs only once.
    /// </summary>
    private void StartInitializationAsync()
    {
        lock (_initLock)
        {
            if (_initializationTask == null)
            {
                _initializationTask = Task.Run(async () =>
                {
                    try
                    {
                        await LoadConfigAndConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during UniFi connection service initialization");
                    }
                });
            }
        }
    }

    /// <summary>
    /// Loads configuration from database and optionally auto-connects.
    /// </summary>
    private async Task LoadConfigAndConnectAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

            var settings = await repository.GetUniFiConnectionSettingsAsync();

            if (settings != null && settings.IsConfigured && !string.IsNullOrEmpty(settings.ControllerUrl))
            {
                _settings = settings;
                _cacheTime = DateTime.UtcNow;

                _logger.LogInformation("Loaded saved UniFi configuration for {Url}", settings.ControllerUrl);

                // Auto-connect if we have credentials and RememberCredentials is true
                if (settings.RememberCredentials && settings.HasCredentials)
                {
                    await Task.Delay(2000); // Wait for app startup
                    await ConnectWithSettingsAsync(settings);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading UniFi configuration from database");
        }
        finally
        {
            IsInitialized = true;
        }
    }

    /// <summary>
    /// Ensures initialization has completed. Call this before accessing settings
    /// if you need to guarantee config is loaded.
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        var task = _initializationTask;
        if (task != null)
        {
            await task;
        }
    }

    public bool IsConnected => _isConnected && _client != null;
    public bool IsInitialized { get; private set; }
    public string? LastError => _lastError;
    public DateTime? LastConnectedAt => _lastConnectedAt;
    public bool IsUniFiOs => _client?.IsUniFiOs ?? false;

    /// <summary>
    /// Gets the current connection config (for UI display)
    /// </summary>
    public UniFiConnectionConfig? CurrentConfig
    {
        get
        {
            if (_settings == null) return null;
            return new UniFiConnectionConfig
            {
                ControllerUrl = _settings.ControllerUrl ?? "",
                Username = _settings.Username ?? "",
                Password = "", // Never expose password
                Site = _settings.Site,
                RememberCredentials = _settings.RememberCredentials,
                IgnoreControllerSSLErrors = _settings.IgnoreControllerSSLErrors
            };
        }
    }

    /// <summary>
    /// Gets the active UniFi API client, or null if not connected
    /// </summary>
    public UniFiApiClient? Client => _isConnected ? _client : null;

    /// <summary>
    /// Get the stored (decrypted) password for testing connection
    /// </summary>
    public async Task<string?> GetStoredPasswordAsync()
    {
        var settings = await GetSettingsAsync();
        if (!string.IsNullOrEmpty(settings.Password))
        {
            try
            {
                return _credentialProtection.Decrypt(settings.Password);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Get the connection settings from database
    /// </summary>
    public async Task<UniFiConnectionSettings> GetSettingsAsync()
    {
        // Check cache first
        if (_settings != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _settings;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiConnectionSettingsAsync();

        if (settings == null)
        {
            // Create default settings
            settings = new UniFiConnectionSettings
            {
                Site = "default",
                RememberCredentials = true,
                IsConfigured = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveUniFiConnectionSettingsAsync(settings);
        }

        _settings = settings;
        _cacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <summary>
    /// Configure and connect to a UniFi controller
    /// </summary>
    public async Task<bool> ConnectAsync(UniFiConnectionConfig config)
    {
        _logger.LogInformation("Connecting to UniFi controller at {Url}", config.ControllerUrl);

        try
        {
            // Dispose existing client
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            _lastError = null;

            // Create new client
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            _client = new UniFiApiClient(
                clientLogger,
                config.ControllerUrl,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors
            );

            // Attempt to authenticate
            var success = await _client.LoginAsync();

            if (success)
            {
                _isConnected = true;
                _lastConnectedAt = DateTime.UtcNow;

                // Save configuration to database
                await SaveSettingsAsync(config);

                _logger.LogInformation("Successfully connected to UniFi controller (UniFi OS: {IsUniFiOs})", _client.IsUniFiOs);
                return true;
            }
            else
            {
                // Use detailed error from API client if available
                _lastError = _client.LastLoginError ?? "Authentication failed. Check username and password.";
                _logger.LogWarning("Failed to authenticate with UniFi controller");
                _client.Dispose();
                _client = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            _lastError = ParseConnectionException(ex);
            _logger.LogError(ex, "Error connecting to UniFi controller");
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    /// <summary>
    /// Connect using existing settings from database
    /// </summary>
    private async Task<bool> ConnectWithSettingsAsync(UniFiConnectionSettings settings)
    {
        if (!settings.HasCredentials) return false;

        try
        {
            // Decrypt password
            var decryptedPassword = _credentialProtection.Decrypt(settings.Password!);

            var config = new UniFiConnectionConfig
            {
                ControllerUrl = settings.ControllerUrl!,
                Username = settings.Username!,
                Password = decryptedPassword,
                Site = settings.Site,
                RememberCredentials = settings.RememberCredentials,
                IgnoreControllerSSLErrors = settings.IgnoreControllerSSLErrors
            };

            // Dispose existing client
            _client?.Dispose();
            _client = null;
            _isConnected = false;
            _lastError = null;

            // Create new client
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            _client = new UniFiApiClient(
                clientLogger,
                config.ControllerUrl,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors
            );

            var success = await _client.LoginAsync();

            if (success)
            {
                _isConnected = true;
                _lastConnectedAt = DateTime.UtcNow;

                // Update last connected timestamp in DB
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();
                var dbSettings = await repository.GetUniFiConnectionSettingsAsync();
                if (dbSettings != null)
                {
                    dbSettings.LastConnectedAt = DateTime.UtcNow;
                    dbSettings.LastError = null;
                    dbSettings.UpdatedAt = DateTime.UtcNow;
                    await repository.SaveUniFiConnectionSettingsAsync(dbSettings);
                }

                _logger.LogInformation("Successfully connected to UniFi controller (UniFi OS: {IsUniFiOs})", _client.IsUniFiOs);
                return true;
            }
            else
            {
                // Use detailed error from API client if available
                _lastError = _client.LastLoginError ?? "Authentication failed. Check username and password.";
                _client.Dispose();
                _client = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            _lastError = ParseConnectionException(ex);
            _logger.LogError(ex, "Error connecting to UniFi controller");
            _client?.Dispose();
            _client = null;
            return false;
        }
    }

    /// <summary>
    /// Save connection settings to database
    /// </summary>
    private async Task SaveSettingsAsync(UniFiConnectionConfig config)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

            var settings = await repository.GetUniFiConnectionSettingsAsync() ?? new UniFiConnectionSettings
            {
                CreatedAt = DateTime.UtcNow
            };

            settings.ControllerUrl = config.ControllerUrl;
            settings.Username = config.Username;
            settings.Site = config.Site;
            settings.RememberCredentials = config.RememberCredentials;
            settings.IgnoreControllerSSLErrors = config.IgnoreControllerSSLErrors;
            settings.IsConfigured = true;
            settings.LastConnectedAt = DateTime.UtcNow;
            settings.LastError = null;
            settings.UpdatedAt = DateTime.UtcNow;

            // Encrypt password before saving
            if (!string.IsNullOrEmpty(config.Password))
            {
                settings.Password = _credentialProtection.Encrypt(config.Password);
            }

            await repository.SaveUniFiConnectionSettingsAsync(settings);

            // Update cache
            _settings = settings;
            _cacheTime = DateTime.UtcNow;

            _logger.LogInformation("Saved UniFi configuration to database");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving UniFi configuration to database");
        }
    }

    /// <summary>
    /// Disconnect from the controller
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_client != null)
        {
            try
            {
                await _client.LogoutAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during logout");
            }

            _client.Dispose();
            _client = null;
        }

        _isConnected = false;
        _logger.LogInformation("Disconnected from UniFi controller");
    }

    /// <summary>
    /// Test connection without saving
    /// </summary>
    public async Task<(bool Success, string? Error, string? ControllerInfo)> TestConnectionAsync(UniFiConnectionConfig config)
    {
        _logger.LogInformation("Testing connection to UniFi controller at {Url}", config.ControllerUrl);

        UniFiApiClient? testClient = null;
        try
        {
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            testClient = new UniFiApiClient(
                clientLogger,
                config.ControllerUrl,
                config.Username,
                config.Password,
                config.Site,
                config.IgnoreControllerSSLErrors
            );

            var success = await testClient.LoginAsync();

            if (success)
            {
                // Get system info for display
                var sysInfo = await testClient.GetSystemInfoAsync();
                var info = sysInfo != null
                    ? $"{sysInfo.Name} v{sysInfo.Version} ({(testClient.IsUniFiOs ? "UniFi OS" : "Standalone")})"
                    : "Connected successfully";

                return (true, null, info);
            }
            else
            {
                // Use detailed error from API client if available
                var error = testClient.LastLoginError ?? "Authentication failed. Check username and password.";
                return (false, error, null);
            }
        }
        catch (Exception ex)
        {
            // Parse common connection errors for user-friendly messages
            var error = ParseConnectionException(ex);
            return (false, error, null);
        }
        finally
        {
            testClient?.Dispose();
        }
    }

    /// <summary>
    /// Attempt to reconnect using saved configuration
    /// </summary>
    public async Task<bool> ReconnectAsync()
    {
        var settings = await GetSettingsAsync();

        if (!settings.IsConfigured || !settings.HasCredentials)
        {
            _lastError = "No saved configuration";
            return false;
        }

        return await ConnectWithSettingsAsync(settings);
    }

    /// <summary>
    /// Wait for the connection to be established (for use during app startup).
    /// Polls until connected or timeout is reached.
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">How often to check connection status</param>
    /// <returns>True if connected, false if timeout or no saved credentials</returns>
    public async Task<bool> WaitForConnectionAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        timeout ??= TimeSpan.FromSeconds(3);
        pollInterval ??= TimeSpan.FromMilliseconds(250);

        // If already connected, return immediately
        if (IsConnected) return true;

        // Check if we have saved credentials to connect with
        var settings = await GetSettingsAsync();
        if (!settings.IsConfigured || !settings.HasCredentials || !settings.RememberCredentials)
        {
            // No auto-connect will happen, don't wait
            return false;
        }

        var startTime = DateTime.UtcNow;
        while (DateTime.UtcNow - startTime < timeout)
        {
            if (IsConnected) return true;
            await Task.Delay(pollInterval.Value);
        }

        _logger.LogWarning("Timed out waiting for UniFi controller connection");
        return false;
    }

    /// <summary>
    /// Clear saved credentials from database
    /// </summary>
    public async Task ClearCredentialsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiConnectionSettingsAsync();
        if (settings != null)
        {
            settings.Username = null;
            settings.Password = null;
            settings.IsConfigured = false;
            settings.UpdatedAt = DateTime.UtcNow;
            await repository.SaveUniFiConnectionSettingsAsync(settings);
        }

        // Invalidate cache
        _settings = null;
        _cacheTime = DateTime.MinValue;
    }

    /// <summary>
    /// Get all discovered devices with proper DeviceType enum values.
    /// This is the preferred way to get devices - use this instead of Client.GetDevicesAsync().
    /// </summary>
    public async Task<List<DiscoveredDevice>> GetDiscoveredDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (_client == null || !_isConnected)
        {
            _logger.LogWarning("Cannot get devices - not connected to controller");
            return new List<DiscoveredDevice>();
        }

        // Return cached devices if still fresh
        if (_cachedDevices != null && DateTime.UtcNow - _deviceCacheTime < DeviceCacheDuration)
        {
            _logger.LogDebug("Returning cached device list ({Count} devices)", _cachedDevices.Count);
            return _cachedDevices;
        }

        var discoveryLogger = _loggerFactory.CreateLogger<UniFiDiscovery>();
        var discovery = new UniFiDiscovery(_client, discoveryLogger);
        var devices = await discovery.DiscoverDevicesAsync(cancellationToken);

        // Cache the result
        _cachedDevices = devices;
        _deviceCacheTime = DateTime.UtcNow;

        return devices;
    }

    /// <summary>
    /// Invalidates the device cache, forcing a fresh fetch on next request.
    /// </summary>
    public void InvalidateDeviceCache()
    {
        _cachedDevices = null;
        _deviceCacheTime = DateTime.MinValue;
    }

    /// <summary>
    /// Parses connection exceptions for user-friendly error messages
    /// </summary>
    private string ParseConnectionException(Exception ex)
    {
        var message = ex.Message;
        var innerMessage = ex.InnerException?.Message ?? "";

        // SSL certificate errors
        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            innerMessage.Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
            innerMessage.Contains("RemoteCertificate", StringComparison.OrdinalIgnoreCase))
        {
            if (innerMessage.Contains("RemoteCertificateNameMismatch"))
            {
                return "SSL certificate error: The certificate doesn't match the hostname. Enable 'Ignore SSL Errors' in settings, or use the correct hostname.";
            }
            if (innerMessage.Contains("RemoteCertificateChainErrors"))
            {
                return "SSL certificate error: Self-signed or untrusted certificate. Enable 'Ignore SSL Errors' in settings.";
            }
            return "SSL certificate error: Unable to establish secure connection. Enable 'Ignore SSL Errors' in settings.";
        }

        // Connection refused
        if (message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection refused. Check if the controller is running and the URL is correct.";
        }

        // Host not found
        if (message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("host is known", StringComparison.OrdinalIgnoreCase))
        {
            return "Host not found. Check the controller URL.";
        }

        // Timeout
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out. Check network connectivity and firewall settings.";
        }

        return message;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}

public class UniFiConnectionConfig
{
    public string ControllerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Site { get; set; } = "default";
    public bool RememberCredentials { get; set; } = true;
    /// <summary>
    /// Whether to ignore SSL certificate errors when connecting to the controller.
    /// Default is true because UniFi controllers use self-signed certificates.
    /// </summary>
    public bool IgnoreControllerSSLErrors { get; set; } = true;
}

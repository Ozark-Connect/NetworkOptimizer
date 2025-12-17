using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Storage.Services;
using System.Text.Json;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages the UniFi controller connection and configuration persistence.
/// This is a singleton service that maintains the API client across the application.
/// </summary>
public class UniFiConnectionService : IDisposable
{
    private readonly ILogger<UniFiConnectionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CredentialProtectionService _credentialProtection;
    private readonly string _configPath;

    private UniFiApiClient? _client;
    private UniFiConnectionConfig? _config;
    private bool _isConnected;
    private string? _lastError;
    private DateTime? _lastConnectedAt;

    public UniFiConnectionService(ILogger<UniFiConnectionService> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _credentialProtection = new CredentialProtectionService();
        _configPath = Path.Combine(AppContext.BaseDirectory, "data", "unifi-connection.json");

        // Load saved configuration on startup (sync to avoid deadlock)
        LoadConfigSync();
    }

    private void LoadConfigSync()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<UniFiConnectionConfig>(json);

                if (_config != null && !string.IsNullOrEmpty(_config.ControllerUrl))
                {
                    // Decrypt password if encrypted
                    if (!string.IsNullOrEmpty(_config.Password))
                    {
                        _config.Password = _credentialProtection.Decrypt(_config.Password);
                    }

                    _logger.LogInformation("Loaded saved UniFi configuration for {Url}", _config.ControllerUrl);

                    // Auto-connect in background if we have credentials
                    if (!string.IsNullOrEmpty(_config.Username) && !string.IsNullOrEmpty(_config.Password))
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000); // Wait for app startup
                            await ConnectAsync(_config);
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading UniFi configuration");
        }
    }

    public bool IsConnected => _isConnected && _client != null;
    public string? LastError => _lastError;
    public DateTime? LastConnectedAt => _lastConnectedAt;
    public UniFiConnectionConfig? CurrentConfig => _config;
    public bool IsUniFiOs => _client?.IsUniFiOs ?? false;

    /// <summary>
    /// Gets the active UniFi API client, or null if not connected
    /// </summary>
    public UniFiApiClient? Client => _isConnected ? _client : null;

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
                config.Site
            );

            // Attempt to authenticate
            var success = await _client.LoginAsync();

            if (success)
            {
                _isConnected = true;
                _lastConnectedAt = DateTime.UtcNow;
                _config = config;

                // Save configuration (without password in plain text for security)
                await SaveConfigAsync();

                _logger.LogInformation("Successfully connected to UniFi controller (UniFi OS: {IsUniFiOs})", _client.IsUniFiOs);
                return true;
            }
            else
            {
                _lastError = "Authentication failed. Check username and password.";
                _logger.LogWarning("Failed to authenticate with UniFi controller");
                _client.Dispose();
                _client = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "Error connecting to UniFi controller");
            _client?.Dispose();
            _client = null;
            return false;
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
                config.Site
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
                return (false, "Authentication failed", null);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
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
        if (_config == null)
        {
            _lastError = "No saved configuration";
            return false;
        }

        return await ConnectAsync(_config);
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create a copy with encrypted password for saving
            var configToSave = new UniFiConnectionConfig
            {
                ControllerUrl = _config?.ControllerUrl ?? "",
                Username = _config?.Username ?? "",
                Password = !string.IsNullOrEmpty(_config?.Password)
                    ? _credentialProtection.Encrypt(_config.Password)
                    : "",
                Site = _config?.Site ?? "default",
                RememberCredentials = _config?.RememberCredentials ?? false
            };

            var json = JsonSerializer.Serialize(configToSave, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_configPath, json);

            _logger.LogInformation("Saved UniFi configuration");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving UniFi configuration");
        }
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
}

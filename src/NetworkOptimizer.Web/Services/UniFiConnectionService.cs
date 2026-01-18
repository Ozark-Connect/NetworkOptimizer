using System.Collections.Concurrent;
using System.Text.Json;
using NetworkOptimizer.Core.Interfaces;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages UniFi controller connections for multiple sites.
/// This is a singleton service that maintains a connection pool across the application.
/// Each site has its own connection state, cached devices, and networks.
/// </summary>
public class UniFiConnectionService : IDisposable
{
    private readonly ILogger<UniFiConnectionService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICredentialProtectionService _credentialProtection;

    // Connection pool: siteId -> connection state
    private readonly ConcurrentDictionary<int, SiteConnection> _connections = new();

    // Cache expiry settings
    private static readonly TimeSpan DeviceCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NetworkCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SettingsCacheExpiry = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Event fired when a site's connection state changes (connect, disconnect, or error).
    /// Subscribers should refresh any cached data for that site.
    /// </summary>
    public event Action<int>? OnConnectionChanged;

    public UniFiConnectionService(
        ILogger<UniFiConnectionService> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        ICredentialProtectionService credentialProtection)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _credentialProtection = credentialProtection;
    }

    #region Connection State

    /// <summary>
    /// Gets or creates a connection state for a site.
    /// </summary>
    private SiteConnection GetOrCreateConnection(int siteId)
    {
        return _connections.GetOrAdd(siteId, id => new SiteConnection(id));
    }

    /// <summary>
    /// Check if a site is connected to its UniFi controller.
    /// </summary>
    public bool IsConnected(int siteId)
    {
        return _connections.TryGetValue(siteId, out var conn) && conn.IsConnected && conn.Client != null;
    }

    /// <summary>
    /// Get the last error message for a site.
    /// </summary>
    public string? GetLastError(int siteId)
    {
        return _connections.TryGetValue(siteId, out var conn) ? conn.LastError : null;
    }

    /// <summary>
    /// Get the last connected timestamp for a site.
    /// </summary>
    public DateTime? GetLastConnectedAt(int siteId)
    {
        return _connections.TryGetValue(siteId, out var conn) ? conn.LastConnectedAt : null;
    }

    /// <summary>
    /// Check if a site's connection is UniFi OS based.
    /// </summary>
    public bool IsUniFiOs(int siteId)
    {
        return _connections.TryGetValue(siteId, out var conn) && conn.Client?.IsUniFiOs == true;
    }

    /// <summary>
    /// Gets the active UniFi API client for a site, or null if not connected.
    /// </summary>
    public UniFiApiClient? GetClient(int siteId)
    {
        return _connections.TryGetValue(siteId, out var conn) && conn.IsConnected ? conn.Client : null;
    }

    /// <summary>
    /// Wait for auto-connect to complete for a site (if credentials are saved).
    /// This is useful for UI pages that want to show a spinner while connecting.
    /// </summary>
    public async Task WaitForConnectionAsync(int siteId, int maxWaitMs = 5000)
    {
        var conn = GetOrCreateConnection(siteId);

        // If already connected, return immediately
        if (conn.IsConnected)
            return;

        // Wait briefly for any in-progress connection
        var waited = 0;
        const int checkInterval = 100;
        while (conn.IsConnecting && waited < maxWaitMs)
        {
            await Task.Delay(checkInterval);
            waited += checkInterval;
        }
    }

    /// <summary>
    /// Gets the current connection config for a site (for UI display).
    /// </summary>
    public UniFiConnectionConfig? GetCurrentConfig(int siteId)
    {
        if (!_connections.TryGetValue(siteId, out var conn) || conn.Settings == null)
            return null;

        return new UniFiConnectionConfig
        {
            ControllerUrl = conn.Settings.ControllerUrl ?? "",
            Username = conn.Settings.Username ?? "",
            Password = "", // Never expose password
            UniFiSiteId = conn.Settings.UniFiSiteId,
            RememberCredentials = conn.Settings.RememberCredentials,
            IgnoreControllerSSLErrors = conn.Settings.IgnoreControllerSSLErrors
        };
    }

    #endregion

    #region Settings

    /// <summary>
    /// Get the connection settings for a site from database.
    /// </summary>
    public async Task<UniFiConnectionSettings> GetSettingsAsync(int siteId)
    {
        var conn = GetOrCreateConnection(siteId);

        // Check cache first
        if (conn.Settings != null && DateTime.UtcNow - conn.SettingsCacheTime < SettingsCacheExpiry)
        {
            return conn.Settings;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiConnectionSettingsAsync(siteId);

        if (settings == null)
        {
            // Create default settings for this site
            settings = new UniFiConnectionSettings
            {
                SiteId = siteId,
                UniFiSiteId = "default",
                RememberCredentials = true,
                IsConfigured = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveUniFiConnectionSettingsAsync(siteId, settings);
        }

        conn.Settings = settings;
        conn.SettingsCacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <summary>
    /// Get the stored (decrypted) password for a site.
    /// </summary>
    public async Task<string?> GetStoredPasswordAsync(int siteId)
    {
        var settings = await GetSettingsAsync(siteId);
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

    #endregion

    #region Connect/Disconnect

    /// <summary>
    /// Configure and connect a site to its UniFi controller.
    /// </summary>
    public async Task<bool> ConnectAsync(int siteId, UniFiConnectionConfig config)
    {
        _logger.LogInformation("Connecting site {SiteId} to UniFi controller at {Url}", siteId, config.ControllerUrl);

        var conn = GetOrCreateConnection(siteId);

        try
        {
            conn.IsConnecting = true;

            // Dispose existing client
            conn.Client?.Dispose();
            conn.Client = null;
            conn.IsConnected = false;
            conn.LastError = null;

            // Create new client
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            conn.Client = new UniFiApiClient(
                clientLogger,
                config.ControllerUrl,
                config.Username,
                config.Password,
                config.UniFiSiteId,
                config.IgnoreControllerSSLErrors
            );

            // Attempt to authenticate
            var success = await conn.Client.LoginAsync();

            if (success)
            {
                conn.IsConnected = true;
                conn.LastConnectedAt = DateTime.UtcNow;

                // Save configuration to database
                await SaveSettingsAsync(siteId, config);

                // Clear cached data
                conn.ClearCaches();

                _logger.LogInformation("Site {SiteId} successfully connected to UniFi controller (UniFi OS: {IsUniFiOs})",
                    siteId, conn.Client.IsUniFiOs);

                // Notify subscribers
                OnConnectionChanged?.Invoke(siteId);

                return true;
            }
            else
            {
                conn.LastError = conn.Client.LastLoginError ?? "Authentication failed. Check username and password.";
                _logger.LogWarning("Site {SiteId} failed to authenticate with UniFi controller", siteId);
                conn.Client.Dispose();
                conn.Client = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            conn.LastError = ParseConnectionException(ex);
            _logger.LogError(ex, "Error connecting site {SiteId} to UniFi controller", siteId);
            conn.Client?.Dispose();
            conn.Client = null;
            return false;
        }
        finally
        {
            conn.IsConnecting = false;
        }
    }

    /// <summary>
    /// Connect a site using saved credentials from database.
    /// </summary>
    public async Task<bool> ConnectWithSavedCredentialsAsync(int siteId)
    {
        var settings = await GetSettingsAsync(siteId);

        if (!settings.IsConfigured || !settings.HasCredentials)
        {
            _logger.LogDebug("Site {SiteId} has no saved credentials", siteId);
            return false;
        }

        try
        {
            var decryptedPassword = _credentialProtection.Decrypt(settings.Password!);

            var config = new UniFiConnectionConfig
            {
                ControllerUrl = settings.ControllerUrl!,
                Username = settings.Username!,
                Password = decryptedPassword,
                UniFiSiteId = settings.UniFiSiteId,
                RememberCredentials = settings.RememberCredentials,
                IgnoreControllerSSLErrors = settings.IgnoreControllerSSLErrors
            };

            var conn = GetOrCreateConnection(siteId);

            // Dispose existing client
            conn.Client?.Dispose();
            conn.Client = null;
            conn.IsConnected = false;
            conn.LastError = null;

            // Create new client
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            conn.Client = new UniFiApiClient(
                clientLogger,
                config.ControllerUrl,
                config.Username,
                config.Password,
                config.UniFiSiteId,
                config.IgnoreControllerSSLErrors
            );

            var success = await conn.Client.LoginAsync();

            if (success)
            {
                conn.IsConnected = true;
                conn.LastConnectedAt = DateTime.UtcNow;

                // Update last connected timestamp in DB
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();
                var dbSettings = await repository.GetUniFiConnectionSettingsAsync(siteId);
                if (dbSettings != null)
                {
                    dbSettings.LastConnectedAt = DateTime.UtcNow;
                    dbSettings.LastError = null;
                    dbSettings.UpdatedAt = DateTime.UtcNow;
                    await repository.SaveUniFiConnectionSettingsAsync(siteId, dbSettings);
                }

                _logger.LogInformation("Site {SiteId} successfully connected to UniFi controller (UniFi OS: {IsUniFiOs})",
                    siteId, conn.Client.IsUniFiOs);
                return true;
            }
            else
            {
                conn.LastError = conn.Client.LastLoginError ?? "Authentication failed. Check username and password.";
                conn.Client.Dispose();
                conn.Client = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            var conn = GetOrCreateConnection(siteId);
            conn.LastError = ParseConnectionException(ex);
            _logger.LogError(ex, "Error connecting site {SiteId} to UniFi controller", siteId);
            conn.Client?.Dispose();
            conn.Client = null;
            return false;
        }
    }

    /// <summary>
    /// Disconnect a site from its UniFi controller.
    /// </summary>
    public async Task DisconnectAsync(int siteId)
    {
        if (!_connections.TryGetValue(siteId, out var conn))
            return;

        if (conn.Client != null)
        {
            try
            {
                await conn.Client.LogoutAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during logout for site {SiteId}", siteId);
            }

            conn.Client.Dispose();
            conn.Client = null;
        }

        conn.IsConnected = false;
        conn.ClearCaches();
        _logger.LogInformation("Site {SiteId} disconnected from UniFi controller", siteId);
        OnConnectionChanged?.Invoke(siteId);
    }

    /// <summary>
    /// Attempt to reconnect a site using saved configuration.
    /// </summary>
    public async Task<bool> ReconnectAsync(int siteId)
    {
        return await ConnectWithSavedCredentialsAsync(siteId);
    }

    /// <summary>
    /// Save connection settings to database.
    /// </summary>
    private async Task SaveSettingsAsync(int siteId, UniFiConnectionConfig config)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

            var settings = await repository.GetUniFiConnectionSettingsAsync(siteId) ?? new UniFiConnectionSettings
            {
                SiteId = siteId,
                CreatedAt = DateTime.UtcNow
            };

            settings.ControllerUrl = config.ControllerUrl;
            settings.Username = config.Username;
            settings.UniFiSiteId = config.UniFiSiteId;
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

            await repository.SaveUniFiConnectionSettingsAsync(siteId, settings);

            // Update cache
            var conn = GetOrCreateConnection(siteId);
            conn.Settings = settings;
            conn.SettingsCacheTime = DateTime.UtcNow;

            _logger.LogInformation("Saved UniFi configuration for site {SiteId}", siteId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving UniFi configuration for site {SiteId}", siteId);
        }
    }

    /// <summary>
    /// Clear saved credentials for a site.
    /// </summary>
    public async Task ClearCredentialsAsync(int siteId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUniFiRepository>();

        var settings = await repository.GetUniFiConnectionSettingsAsync(siteId);
        if (settings != null)
        {
            settings.Username = null;
            settings.Password = null;
            settings.IsConfigured = false;
            settings.UpdatedAt = DateTime.UtcNow;
            await repository.SaveUniFiConnectionSettingsAsync(siteId, settings);
        }

        // Invalidate cache
        if (_connections.TryGetValue(siteId, out var conn))
        {
            conn.Settings = null;
            conn.SettingsCacheTime = DateTime.MinValue;
        }
    }

    #endregion

    #region Test Connection

    /// <summary>
    /// Test connection without saving.
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
                config.UniFiSiteId,
                config.IgnoreControllerSSLErrors
            );

            var success = await testClient.LoginAsync();

            if (success)
            {
                var sysInfo = await testClient.GetSystemInfoAsync();
                var info = sysInfo != null
                    ? $"{sysInfo.Name} v{sysInfo.Version} ({(testClient.IsUniFiOs ? "UniFi OS" : "Standalone")})"
                    : "Connected successfully";

                return (true, null, info);
            }
            else
            {
                var error = testClient.LastLoginError ?? "Authentication failed. Check username and password.";
                return (false, error, null);
            }
        }
        catch (Exception ex)
        {
            var error = ParseConnectionException(ex);
            return (false, error, null);
        }
        finally
        {
            testClient?.Dispose();
        }
    }

    /// <summary>
    /// Get list of available sites from the controller using provided credentials.
    /// </summary>
    public async Task<(bool Success, string? Error, List<UniFiSite> Sites)> GetUniFiSitesAsync(UniFiConnectionConfig config)
    {
        _logger.LogInformation("Fetching UniFi sites from controller at {Url}", config.ControllerUrl);

        UniFiApiClient? testClient = null;
        try
        {
            var clientLogger = _loggerFactory.CreateLogger<UniFiApiClient>();
            testClient = new UniFiApiClient(
                clientLogger,
                config.ControllerUrl,
                config.Username,
                config.Password,
                config.UniFiSiteId,
                config.IgnoreControllerSSLErrors
            );

            var success = await testClient.LoginAsync();

            if (!success)
            {
                var error = testClient.LastLoginError ?? "Authentication failed. Check username and password.";
                return (false, error, new List<UniFiSite>());
            }

            var sitesDoc = await testClient.GetSitesAsync();
            if (sitesDoc == null)
            {
                return (false, "Failed to retrieve sites", new List<UniFiSite>());
            }

            var sites = new List<UniFiSite>();
            if (sitesDoc.RootElement.TryGetProperty("data", out var dataArray))
            {
                foreach (var siteElement in dataArray.EnumerateArray())
                {
                    var site = new UniFiSite
                    {
                        Name = siteElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                        Description = siteElement.TryGetProperty("desc", out var desc) ? desc.GetString() ?? "" : "",
                        Role = siteElement.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "",
                        DeviceCount = siteElement.TryGetProperty("device_count", out var count) ? count.GetInt32() : 0
                    };
                    sites.Add(site);
                }
            }

            _logger.LogInformation("Found {Count} UniFi sites", sites.Count);
            return (true, null, sites);
        }
        catch (Exception ex)
        {
            var error = ParseConnectionException(ex);
            return (false, error, new List<UniFiSite>());
        }
        finally
        {
            testClient?.Dispose();
        }
    }

    #endregion

    #region Device and Network Discovery

    /// <summary>
    /// Get all discovered devices for a site with proper DeviceType enum values.
    /// </summary>
    public async Task<List<DiscoveredDevice>> GetDiscoveredDevicesAsync(int siteId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(siteId, out var conn) || conn.Client == null || !conn.IsConnected)
        {
            _logger.LogWarning("Cannot get devices for site {SiteId} - not connected", siteId);
            return new List<DiscoveredDevice>();
        }

        // Return cached devices if still fresh
        if (conn.CachedDevices != null && DateTime.UtcNow - conn.DeviceCacheTime < DeviceCacheDuration)
        {
            _logger.LogDebug("Returning cached device list for site {SiteId} ({Count} devices)", siteId, conn.CachedDevices.Count);
            return conn.CachedDevices;
        }

        var discoveryLogger = _loggerFactory.CreateLogger<UniFiDiscovery>();
        var discovery = new UniFiDiscovery(conn.Client, discoveryLogger);
        var devices = await discovery.DiscoverDevicesAsync(cancellationToken);

        // Cache the result
        conn.CachedDevices = devices;
        conn.DeviceCacheTime = DateTime.UtcNow;

        return devices;
    }

    /// <summary>
    /// Invalidates the device cache for a site, forcing a fresh fetch on next request.
    /// </summary>
    public void InvalidateDeviceCache(int siteId)
    {
        if (_connections.TryGetValue(siteId, out var conn))
        {
            conn.CachedDevices = null;
            conn.DeviceCacheTime = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Gets the list of configured networks for a site from the UniFi controller.
    /// </summary>
    public async Task<List<NetworkInfo>> GetNetworksAsync(int siteId, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(siteId, out var conn) || conn.Client == null || !conn.IsConnected)
        {
            _logger.LogWarning("Cannot get networks for site {SiteId} - not connected", siteId);
            return new List<NetworkInfo>();
        }

        // Return cached networks if still fresh
        if (conn.CachedNetworks != null && DateTime.UtcNow - conn.NetworkCacheTime < NetworkCacheDuration)
        {
            return conn.CachedNetworks;
        }

        var discoveryLogger = _loggerFactory.CreateLogger<UniFiDiscovery>();
        var discovery = new UniFiDiscovery(conn.Client, discoveryLogger);
        var topology = await discovery.DiscoverTopologyAsync(cancellationToken);

        // Cache the result
        conn.CachedNetworks = topology.Networks;
        conn.NetworkCacheTime = DateTime.UtcNow;

        return conn.CachedNetworks;
    }

    /// <summary>
    /// Clears all cached data for a site.
    /// </summary>
    public void ClearCaches(int siteId)
    {
        if (_connections.TryGetValue(siteId, out var conn))
        {
            conn.ClearCaches();
            _logger.LogDebug("Cleared caches for site {SiteId}", siteId);
        }
    }

    #endregion

    #region Speed Test Enrichment

    /// <summary>
    /// Enrich a speed test result with client info from UniFi (MAC, name, Wi-Fi signal).
    /// </summary>
    public async Task EnrichSpeedTestWithClientInfoAsync(int siteId, Iperf3Result result, bool setDeviceName = true, bool overwriteMac = true)
    {
        if (!_connections.TryGetValue(siteId, out var conn) || conn.Client == null || !conn.IsConnected)
            return;

        try
        {
            var clients = await conn.Client.GetClientsAsync();
            var client = clients?.FirstOrDefault(c => c.Ip == result.DeviceHost);

            // If IP match failed, try matching by MAC
            if (client == null && !string.IsNullOrEmpty(result.ClientMac))
            {
                client = clients?.FirstOrDefault(c =>
                    c.Mac.Equals(result.ClientMac, StringComparison.OrdinalIgnoreCase));
            }

            if (client == null)
                return;

            // Set MAC address
            if (overwriteMac || string.IsNullOrEmpty(result.ClientMac))
                result.ClientMac = client.Mac;

            // Set device name from UniFi
            if (setDeviceName)
                result.DeviceName = !string.IsNullOrEmpty(client.Name) ? client.Name : client.Hostname;

            // Capture Wi-Fi signal for wireless clients
            if (!client.IsWired)
            {
                result.WifiSignalDbm = client.Signal;
                result.WifiNoiseDbm = client.Noise;
                result.WifiChannel = client.Channel;
                result.WifiRadioProto = client.RadioProto;
                result.WifiRadio = client.Radio;
                result.WifiTxRateKbps = client.TxRate;
                result.WifiRxRateKbps = client.RxRate;

                // Capture MLO (Multi-Link Operation) data for Wi-Fi 7 clients
                result.WifiIsMlo = client.IsMlo ?? false;
                if (client.IsMlo == true && client.MloDetails?.Count > 0)
                {
                    var mloLinks = client.MloDetails.Select(m => new
                    {
                        radio = m.Radio,
                        channel = m.Channel,
                        channelWidth = m.ChannelWidth,
                        signal = m.Signal,
                        noise = m.Noise,
                        txRate = m.TxRate,
                        rxRate = m.RxRate
                    }).ToList();
                    result.WifiMloLinksJson = JsonSerializer.Serialize(mloLinks);
                }

                _logger.LogDebug("Enriched Wi-Fi info for site {SiteId}, {Ip}: Signal={Signal}dBm",
                    siteId, result.DeviceHost, result.WifiSignalDbm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich client info for site {SiteId}, {Ip}", siteId, result.DeviceHost);
        }
    }

    /// <summary>
    /// Enrich a speed test result with client info from UniFi, searching across all connected sites.
    /// Used for client-initiated tests where the site is unknown.
    /// </summary>
    public async Task EnrichSpeedTestWithClientInfoAsync(Iperf3Result result, bool setDeviceName = true, bool overwriteMac = true)
    {
        // Try each connected site until we find the client
        foreach (var siteId in _connections.Keys)
        {
            if (IsConnected(siteId))
            {
                await EnrichSpeedTestWithClientInfoAsync(siteId, result, setDeviceName, overwriteMac);
                // If we found client info (MAC was set), stop searching
                if (!string.IsNullOrEmpty(result.ClientMac))
                    return;
            }
        }
    }

    /// <summary>
    /// Check if any site is connected. Used by services that don't have site context.
    /// </summary>
    public bool IsAnyConnected()
    {
        return _connections.Values.Any(c => c.IsConnected && c.Client != null);
    }

    /// <summary>
    /// Get any connected client. Used by services that need a client but don't have site context
    /// (e.g., fetching the fingerprint database which is global to UniFi).
    /// </summary>
    public UniFiApiClient? GetAnyConnectedClient()
    {
        return _connections.Values.FirstOrDefault(c => c.IsConnected && c.Client != null)?.Client;
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Parses connection exceptions for user-friendly error messages.
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
        foreach (var conn in _connections.Values)
        {
            conn.Client?.Dispose();
        }
        _connections.Clear();
    }

    #endregion
}

/// <summary>
/// Connection state for a single site.
/// </summary>
internal class SiteConnection
{
    public int SiteId { get; }
    public UniFiApiClient? Client { get; set; }
    public bool IsConnected { get; set; }
    public bool IsConnecting { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastConnectedAt { get; set; }

    // Settings cache
    public UniFiConnectionSettings? Settings { get; set; }
    public DateTime SettingsCacheTime { get; set; } = DateTime.MinValue;

    // Device cache
    public List<DiscoveredDevice>? CachedDevices { get; set; }
    public DateTime DeviceCacheTime { get; set; } = DateTime.MinValue;

    // Network cache
    public List<NetworkInfo>? CachedNetworks { get; set; }
    public DateTime NetworkCacheTime { get; set; } = DateTime.MinValue;

    public SiteConnection(int siteId)
    {
        SiteId = siteId;
    }

    public void ClearCaches()
    {
        CachedDevices = null;
        DeviceCacheTime = DateTime.MinValue;
        CachedNetworks = null;
        NetworkCacheTime = DateTime.MinValue;
    }
}

/// <summary>
/// Configuration for connecting to a UniFi controller.
/// </summary>
public class UniFiConnectionConfig
{
    public string ControllerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>UniFi site ID within the controller, used in API paths (default: "default")</summary>
    public string UniFiSiteId { get; set; } = "default";
    public bool RememberCredentials { get; set; } = true;
    /// <summary>
    /// Whether to ignore SSL certificate errors when connecting to the controller.
    /// Default is true because UniFi controllers use self-signed certificates.
    /// </summary>
    public bool IgnoreControllerSSLErrors { get; set; } = true;
}

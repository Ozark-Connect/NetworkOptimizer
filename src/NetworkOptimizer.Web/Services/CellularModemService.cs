using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats.
/// Delegates the per-vendor poll mechanics to an ICellularModemProvider
/// resolved by ProviderKey; this class owns the timer, cache, persistence
/// glue, and UniFi auto-discovery.
/// </summary>
public class CellularModemService : ICellularModemService
{
    private readonly ILogger<CellularModemService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiSshService _sshService;
    private readonly UniFiConnectionService _connectionService;
    private readonly Dictionary<string, ICellularModemProvider> _providers;
    private readonly Timer? _pollingTimer;
    private readonly object _lock = new();
    private CellularModemStats? _lastStats;
    private readonly Dictionary<int, CellularModemStats> _statsCache = new();
    private bool _isPolling;

    // Default QMI device path for U5G-Max
    private const string DefaultQmiDevice = "/dev/wwan0qmi0";
    private const int DefaultPollingIntervalSeconds = 300;

    // Default provider key for rows that have an empty Provider column
    // (e.g. legacy rows persisted before this column existed). The
    // EF migration backfills the column with "qmicli" so in practice
    // this is only a defensive fallback.
    private const string DefaultProviderKey = "qmicli";

    public CellularModemService(
        ILogger<CellularModemService> logger,
        IServiceProvider serviceProvider,
        UniFiSshService sshService,
        UniFiConnectionService connectionService,
        IEnumerable<ICellularModemProvider> providers)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshService = sshService;
        _connectionService = connectionService;
        _providers = providers.ToDictionary(p => p.ProviderKey, StringComparer.OrdinalIgnoreCase);

        if (!_providers.ContainsKey(DefaultProviderKey))
        {
            _logger.LogError(
                "Default cellular modem provider '{Key}' is not registered. " +
                "Polling will fail until the provider is added to DI.",
                DefaultProviderKey);
        }

        // Start polling timer (checks every minute, but respects per-modem intervals)
        _pollingTimer = new Timer(state => _ = PollAllModemsAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Get the most recent stats for all modems
    /// </summary>
    public CellularModemStats? GetLastStats()
    {
        lock (_lock)
        {
            return _lastStats;
        }
    }

    /// <summary>
    /// Get cached stats for a specific modem without polling.
    /// Returns null if no cached stats exist for this modem.
    /// </summary>
    public CellularModemStats? GetCachedStats(int modemId)
    {
        lock (_lock)
        {
            return _statsCache.TryGetValue(modemId, out var stats) ? stats : null;
        }
    }

    /// <summary>
    /// Auto-discover U5G-Max modems from UniFi device list
    /// </summary>
    public async Task<List<DiscoveredModem>> DiscoverModemsAsync()
    {
        var discovered = new List<DiscoveredModem>();

        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            _logger.LogWarning("Cannot discover modems: UniFi controller not connected");
            return discovered;
        }

        try
        {
            var devices = await _connectionService.Client.GetDevicesAsync();

            foreach (var device in devices)
            {
                // Use product database to identify cellular modems
                if (UniFiProductDatabase.IsCellularModem(device.Model, device.Shortname, device.Type))
                {
                    var displayModel = UniFiProductDatabase.GetBestProductName(device.Model, device.Shortname);
                    discovered.Add(new DiscoveredModem
                    {
                        DeviceId = device.Id,
                        Name = device.Name,
                        Model = displayModel,
                        Host = device.Ip ?? "",
                        MacAddress = device.Mac,
                        IsOnline = device.State == 1 && device.Adopted
                    });
                    _logger.LogInformation("Discovered cellular modem: {Name} ({Model}) at {Host}",
                        device.Name, displayModel, device.Ip);
                }
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering modems from UniFi controller");
        }

        return discovered;
    }

    /// <summary>
    /// Dispatch a poll to the appropriate provider based on the row's Provider column.
    /// Empty Provider falls back to DefaultProviderKey so legacy rows keep polling.
    /// </summary>
    private async Task<CellularModemStats?> ExecutePollAsync(ModemConfiguration modem)
    {
        var providerKey = string.IsNullOrWhiteSpace(modem.Provider)
            ? DefaultProviderKey
            : modem.Provider;

        if (!_providers.TryGetValue(providerKey, out var provider))
        {
            _logger.LogError(
                "No cellular modem provider registered for key '{Key}' (modem {Name})",
                providerKey, modem.Name);
            return null;
        }

        var context = ToPollContext(modem);
        var stats = await provider.PollAsync(context);

        if (stats != null)
        {
            lock (_lock)
            {
                _lastStats = stats;
            }
        }

        return stats;
    }

    private static ModemPollContext ToPollContext(ModemConfiguration modem) => new()
    {
        Id = modem.Id,
        Name = modem.Name,
        Host = modem.Host,
        Port = modem.Port,
        Username = string.IsNullOrEmpty(modem.Username) ? null : modem.Username,
        Password = string.IsNullOrEmpty(modem.Password) ? null : modem.Password,
        PrivateKeyPath = string.IsNullOrEmpty(modem.PrivateKeyPath) ? null : modem.PrivateKeyPath,
        ModemType = modem.ModemType,
        TransportPath = modem.QmiDevice,
    };

    /// <summary>
    /// Test SSH connection to a modem using shared credentials
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(string host)
    {
        return await _sshService.TestConnectionAsync(host);
    }

    /// <summary>
    /// Poll a modem - fetches stats via the resolved provider and updates LastPolled timestamp
    /// </summary>
    public async Task<(bool success, string message)> PollModemAsync(ModemConfiguration modem)
    {
        try
        {
            var stats = await ExecutePollAsync(modem);

            if (stats != null)
            {
                // Update LastPolled in database
                await UpdateModemConfigAsync(modem.Id, null);

                lock (_lock)
                {
                    _lastStats = stats;
                    _statsCache[modem.Id] = stats;
                }

                return (true, $"Modem polled successfully. RSRP: {stats.Lte?.Rsrp ?? stats.Nr5g?.Rsrp}dBm");
            }
            else
            {
                await UpdateModemConfigAsync(modem.Id, "Poll returned no data");
                return (false, "Failed to poll modem - no data returned");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing modem {Name}", modem.Name);
            await UpdateModemConfigAsync(modem.Id, ex.Message);
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get all configured modems (legacy)
    /// </summary>
    public async Task<List<ModemConfiguration>> GetModemsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        return await repository.GetModemConfigurationsAsync();
    }

    /// <summary>
    /// Add or update a modem configuration (simplified - no SSH creds needed)
    /// </summary>
    public async Task<ModemConfiguration> SaveModemAsync(ModemConfiguration config)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        await repository.SaveModemConfigurationAsync(config);
        return config;
    }

    /// <summary>
    /// Delete a modem configuration
    /// </summary>
    public async Task DeleteModemAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        await repository.DeleteModemConfigurationAsync(id);

        // Clear cached stats since the modem may have been the one producing them
        _lastStats = null;
    }

    private async Task PollAllModemsAsync()
    {
        if (_isPolling) return;

        try
        {
            _isPolling = true;

            // qmicli modems require SSH credentials; other providers (e.g. the
            // HTTP-based Netgear Nighthawk hotspot) handle their own auth. Fetch
            // SSH availability once per cycle so qmicli modems skip gracefully
            // when SSH isn't configured, without blocking the entire poll loop.
            var sshSettings = await _sshService.GetSettingsAsync();
            var sshAvailable = sshSettings.Enabled && sshSettings.HasCredentials;

            // Only poll configured and enabled modems (not auto-discovered ones)
            // Auto-discovered modems must be added to config before they're polled
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();

            var modems = await repository.GetEnabledModemConfigurationsAsync();

            foreach (var modem in modems)
            {
                var providerKey = string.IsNullOrWhiteSpace(modem.Provider)
                    ? DefaultProviderKey
                    : modem.Provider;

                // Skip qmicli modems if SSH isn't configured - other providers handle their own auth
                if (providerKey == DefaultProviderKey && !sshAvailable)
                {
                    continue;
                }

                // Check if it's time to poll this modem
                if (modem.LastPolled.HasValue)
                {
                    var elapsed = DateTime.UtcNow - modem.LastPolled.Value;
                    if (elapsed.TotalSeconds < modem.PollingIntervalSeconds)
                        continue;
                }

                await PollModemAsync(modem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in modem polling timer");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task UpdateModemConfigAsync(int modemId, string? error)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();

            var config = await repository.GetModemConfigurationAsync(modemId);
            if (config != null)
            {
                config.LastPolled = DateTime.UtcNow;
                config.LastError = error;
                config.UpdatedAt = DateTime.UtcNow;
                await repository.SaveModemConfigurationAsync(config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update modem config after poll");
        }
    }

    public void Dispose()
    {
        _pollingTimer?.Dispose();
    }
}

/// <summary>
/// Represents a discovered cellular modem from UniFi
/// </summary>
public class DiscoveredModem
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Host { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public bool IsOnline { get; set; }
}

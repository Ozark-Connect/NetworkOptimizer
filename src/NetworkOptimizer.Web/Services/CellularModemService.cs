using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
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
    private readonly MonitoringInfluxClient _influx;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly Dictionary<string, ICellularModemProvider> _providers;
    private readonly Timer? _pollingTimer;
    private readonly object _lock = new();
    private CellularModemStats? _lastStats;
    private readonly Dictionary<int, CellularModemStats> _statsCache = new();
    private bool _isPolling;

    private const string DefaultProviderKey = "qmicli";

    public CellularModemService(
        ILogger<CellularModemService> logger,
        IServiceProvider serviceProvider,
        UniFiSshService sshService,
        UniFiConnectionService connectionService,
        MonitoringInfluxClient influx,
        ICredentialProtectionService credentialProtection,
        IEnumerable<ICellularModemProvider> providers)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshService = sshService;
        _connectionService = connectionService;
        _influx = influx;
        _credentialProtection = credentialProtection;
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
    /// Auto-discover UniFi cellular modems from the controller device list
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
    /// Resolve the ICellularModemProvider for a modem configuration.
    /// Falls back to DefaultProviderKey for legacy rows with empty Provider.
    /// </summary>
    private ICellularModemProvider? ResolveProvider(ModemConfiguration modem)
    {
        var providerKey = string.IsNullOrWhiteSpace(modem.Provider)
            ? DefaultProviderKey
            : modem.Provider;

        if (_providers.TryGetValue(providerKey, out var provider))
            return provider;

        _logger.LogError(
            "No cellular modem provider registered for key '{Key}' (modem {Name})",
            providerKey, modem.Name);
        return null;
    }

    // TODO: Move to ICellularModemProvider.RequiresSharedSsh property when adding a 4th provider
    /// <summary>
    /// Whether this modem's provider requires the shared UniFi SSH credentials.
    /// Providers with per-modem SSH credentials (quectel-at) handle their own auth.
    /// </summary>
    private static bool RequiresSharedSsh(ModemConfiguration modem)
    {
        var key = string.IsNullOrWhiteSpace(modem.Provider) ? DefaultProviderKey : modem.Provider;
        return string.Equals(key, "qmicli", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dispatch a poll to the appropriate provider based on the row's Provider column.
    /// </summary>
    private async Task<CellularModemStats?> ExecutePollAsync(ModemConfiguration modem)
    {
        var provider = ResolveProvider(modem);
        if (provider == null) return null;

        var context = ToPollContext(modem);
        return await provider.PollAsync(context);
    }

    private ModemPollContext ToPollContext(ModemConfiguration modem)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(modem.Password))
        {
            try { password = _credentialProtection.Decrypt(modem.Password); }
            catch { password = modem.Password; }
        }

        return new ModemPollContext
        {
            Id = modem.Id,
            Name = modem.Name,
            Host = modem.Host,
            Port = modem.Port,
            Username = string.IsNullOrEmpty(modem.Username) ? null : modem.Username,
            Password = password,
            PrivateKeyPath = string.IsNullOrEmpty(modem.PrivateKeyPath) ? null : modem.PrivateKeyPath,
            ModemType = modem.ModemType,
            TransportPath = modem.QmiDevice,
        };
    }

    /// <summary>
    /// Provider-aware probe. Resolves the provider for the configuration
    /// and asks it to verify reachability and (where applicable) auth.
    /// Used by the Settings page Probe & Detect button.
    /// </summary>
    public async Task<(bool success, string message)> ProbeModemAsync(ModemConfiguration modem)
    {
        var provider = ResolveProvider(modem);
        if (provider == null)
            return (false, $"No provider registered for '{modem.Provider}'");

        var context = ToPollContext(modem);
        return await provider.TestConnectionAsync(context);
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
                await UpdateModemConfigAsync(modem.Id, null, success: true);

                lock (_lock)
                {
                    _lastStats = stats;
                    _statsCache[modem.Id] = stats;
                }

                // Write to InfluxDB for time-series charting
                WriteCellularToInflux(modem, stats);

                return (true, $"Modem polled successfully. RSRP: {stats.Lte?.Rsrp ?? stats.Nr5g?.Rsrp}dBm");
            }
            else
            {
                await UpdateModemConfigAsync(modem.Id, "Poll returned no data", success: false);
                return (false, "Failed to poll modem - no data returned");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing modem {Name}", modem.Name);
            await UpdateModemConfigAsync(modem.Id, ex.Message, success: false);
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get all configured modems
    /// </summary>
    public async Task<List<ModemConfiguration>> GetModemsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        return await repository.GetModemConfigurationsAsync();
    }

    /// <summary>
    /// Add or update a modem configuration.
    /// Encrypts the password before persisting.
    /// </summary>
    public async Task<ModemConfiguration> SaveModemAsync(ModemConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.Password) && !_credentialProtection.IsEncrypted(config.Password))
        {
            config.Password = _credentialProtection.Encrypt(config.Password);
        }

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

        lock (_lock)
        {
            _statsCache.Remove(id);
            _lastStats = null;
        }
    }

    private async Task PollAllModemsAsync()
    {
        if (_isPolling) return;

        try
        {
            _isPolling = true;

            // SSH-based providers require SSH credentials; HTTP-based providers
            // handle their own auth. Check once per cycle so SSH modems skip
            // gracefully without blocking the entire poll loop.
            var sshSettings = await _sshService.GetSettingsAsync();
            var sshAvailable = sshSettings.Enabled && sshSettings.HasCredentials;

            // Only poll configured and enabled modems (not auto-discovered ones)
            // Auto-discovered modems must be added to config before they're polled
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();

            var modems = await repository.GetEnabledModemConfigurationsAsync();

            foreach (var modem in modems)
            {
                if (RequiresSharedSsh(modem) && !sshAvailable)
                    continue;

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

    private async Task UpdateModemConfigAsync(int modemId, string? error, bool success)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();

            var config = await repository.GetModemConfigurationAsync(modemId);
            if (config != null)
            {
                if (success)
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

    /// <summary>
    /// Write cellular signal metrics to InfluxDB for time-series charting.
    /// In NSA mode, writes separate points for LTE and NR5G so both bands
    /// are charted independently. Fire-and-forget; InfluxDB client handles
    /// buffering and graceful skip when not configured.
    /// </summary>
    private void WriteCellularToInflux(ModemConfiguration modem, CellularModemStats stats)
    {
        var modemId = modem.Id.ToString();
        var provider = modem.Provider ?? "qmicli";

        // Write NR5G signal if present
        if (stats.Nr5g?.Rsrp.HasValue == true)
        {
            _ = _influx.WriteCellularAsync(
                modemId: modemId,
                modemName: stats.ModemName,
                provider: provider,
                networkMode: stats.NetworkModeLabel,
                carrier: stats.Carrier,
                bandName: stats.ActiveBand?.BandName,
                channel: stats.ActiveBand?.Channel,
                bandwidthMhz: stats.ActiveBand?.BandwidthMhz,
                rsrp: stats.Nr5g.Rsrp,
                rsrq: stats.Nr5g.Rsrq,
                snr: stats.Nr5g.Snr,
                rssi: stats.Nr5g.Rssi,
                signalQuality: stats.SignalQuality,
                signalBars: stats.Nr5g.Bars,
                isRoaming: stats.IsRoaming,
                timestamp: stats.Timestamp);
        }

        // Write LTE signal (always present in LTE-only and NSA modes)
        if (stats.Lte?.Rsrp.HasValue == true)
        {
            // Offset by 1 tick so InfluxDB doesn't overwrite the NR5G point
            _ = _influx.WriteCellularAsync(
                modemId: modemId,
                modemName: stats.ModemName,
                provider: provider,
                networkMode: "LTE",
                carrier: stats.Carrier,
                bandName: null,
                channel: null,
                bandwidthMhz: null,
                rsrp: stats.Lte.Rsrp,
                rsrq: stats.Lte.Rsrq,
                snr: stats.Lte.Snr,
                rssi: stats.Lte.Rssi,
                signalQuality: null,
                signalBars: stats.Lte.Bars,
                isRoaming: stats.IsRoaming,
                timestamp: stats.Timestamp.AddTicks(1));
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

using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats via SSH.
/// Uses shared UniFiSshService for SSH operations.
/// Auto-discovers U5G-Max modems from UniFi device list.
/// </summary>
public class CellularModemService : IDisposable
{
    private readonly ILogger<CellularModemService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiSshService _sshService;
    private readonly UniFiConnectionService _connectionService;
    private readonly Timer? _pollingTimer;
    private readonly object _lock = new();
    private CellularModemStats? _lastStats;
    private bool _isPolling;

    // Default QMI device path for U5G-Max
    private const string DefaultQmiDevice = "/dev/wwan0qmi0";
    private const int DefaultPollingIntervalSeconds = 300;

    public CellularModemService(
        ILogger<CellularModemService> logger,
        IServiceProvider serviceProvider,
        UniFiSshService sshService,
        UniFiConnectionService connectionService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshService = sshService;
        _connectionService = connectionService;

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
                // Look for U5G-Max devices - check model, shortname, and type
                var model = device.Model?.ToUpperInvariant() ?? "";
                var shortname = device.Shortname?.ToUpperInvariant() ?? "";
                var type = device.Type?.ToUpperInvariant() ?? "";

                // U5G-Max appears as shortname "U5GMAX" or type "umbb"
                bool isCellularModem = model.Contains("U5G") || model.Contains("ULTE") || model.Contains("U-LTE") ||
                                       shortname.Contains("U5G") || shortname.Contains("ULTE") || shortname.Contains("U-LTE") ||
                                       type.Contains("UMBB") || type == "LTE";

                if (isCellularModem)
                {
                    var rawModel = !string.IsNullOrEmpty(device.Shortname) ? device.Shortname : device.Model ?? "Unknown";
                    var displayModel = FormatModelName(rawModel);
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
    /// Poll a modem by host IP using shared SSH credentials
    /// </summary>
    public async Task<CellularModemStats?> PollModemAsync(string host, string name, string qmiDevice = DefaultQmiDevice)
    {
        _logger.LogInformation("Polling modem {Name} at {Host}", name, host);

        try
        {
            var stats = new CellularModemStats
            {
                ModemHost = host,
                ModemName = name,
                Timestamp = DateTime.UtcNow
            };

            // Run all qmicli commands using shared SSH service
            var signalTask = _sshService.RunCommandAsync(host, $"qmicli -d {qmiDevice} --device-open-proxy --nas-get-signal-info");
            var servingTask = _sshService.RunCommandAsync(host, $"qmicli -d {qmiDevice} --device-open-proxy --nas-get-serving-system");
            var cellTask = _sshService.RunCommandAsync(host, $"qmicli -d {qmiDevice} --device-open-proxy --nas-get-cell-location-info");
            var bandTask = _sshService.RunCommandAsync(host, $"qmicli -d {qmiDevice} --device-open-proxy --nas-get-rf-band-info");

            await Task.WhenAll(signalTask, servingTask, cellTask, bandTask);

            // Parse signal info
            if (signalTask.Result.success)
            {
                var (lte, nr5g) = QmicliParser.ParseSignalInfo(signalTask.Result.output);
                stats.Lte = lte;
                stats.Nr5g = nr5g;
            }

            // Parse serving system
            if (servingTask.Result.success)
            {
                var (regState, carrier, mcc, mnc, roaming) = QmicliParser.ParseServingSystem(servingTask.Result.output);
                stats.RegistrationState = regState;
                stats.Carrier = carrier;
                stats.CarrierMcc = mcc;
                stats.CarrierMnc = mnc;
                stats.IsRoaming = roaming;
            }

            // Parse cell location info
            if (cellTask.Result.success)
            {
                var (servingCell, neighbors) = QmicliParser.ParseCellLocationInfo(cellTask.Result.output);
                stats.ServingCell = servingCell;
                stats.NeighborCells = neighbors;
            }

            // Parse band info
            if (bandTask.Result.success)
            {
                stats.ActiveBand = QmicliParser.ParseRfBandInfo(bandTask.Result.output);
            }

            // Update last stats
            lock (_lock)
            {
                _lastStats = stats;
            }

            _logger.LogInformation("Successfully polled modem {Name}: {Carrier}, Signal Quality: {Quality}%",
                name, stats.Carrier, stats.SignalQuality);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling modem {Name}", name);
            return null;
        }
    }

    /// <summary>
    /// Poll a modem using legacy ModemConfiguration (for backward compatibility)
    /// </summary>
    public async Task<CellularModemStats?> PollModemAsync(ModemConfiguration config)
    {
        return await PollModemAsync(config.Host, config.Name, config.QmiDevice);
    }

    /// <summary>
    /// Test SSH connection to a modem using shared credentials
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(string host)
    {
        return await _sshService.TestConnectionAsync(host);
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
    }

    private async Task PollAllModemsAsync()
    {
        if (_isPolling) return;

        try
        {
            _isPolling = true;

            // Check if SSH is configured
            var sshSettings = await _sshService.GetSettingsAsync();
            if (!sshSettings.Enabled || !sshSettings.HasCredentials)
            {
                return; // SSH not configured, skip polling
            }

            // Only poll configured and enabled modems (not auto-discovered ones)
            // Auto-discovered modems must be added to config before they're polled
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();

            var modems = await repository.GetEnabledModemConfigurationsAsync();

            foreach (var modem in modems)
            {
                // Check if it's time to poll this modem
                if (modem.LastPolled.HasValue)
                {
                    var elapsed = DateTime.UtcNow - modem.LastPolled.Value;
                    if (elapsed.TotalSeconds < modem.PollingIntervalSeconds)
                        continue;
                }

                await PollModemAsync(modem);
                await UpdateModemConfigAsync(modem.Id, null);
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

    /// <summary>
    /// Format model names for display (e.g., U5GMAX -> U5G-Max)
    /// </summary>
    private static string FormatModelName(string model)
    {
        if (string.IsNullOrEmpty(model))
            return "Unknown";

        return model.ToUpperInvariant() switch
        {
            "U5GMAX" => "U5G-Max",
            "ULTE" => "U-LTE",
            "ULTEPRO" => "U-LTE-Pro",
            _ => model
        };
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

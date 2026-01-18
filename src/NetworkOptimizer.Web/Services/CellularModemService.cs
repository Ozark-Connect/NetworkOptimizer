using System.Collections.Concurrent;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats via SSH.
/// Uses shared UniFiSshService for SSH operations.
/// Auto-discovers U5G-Max modems from UniFi device list.
/// </summary>
public class CellularModemService : ICellularModemService
{
    private readonly ILogger<CellularModemService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiSshService _sshService;
    private readonly UniFiConnectionService _connectionService;
    private readonly Timer? _pollingTimer;
    private readonly ConcurrentDictionary<int, CellularModemStats> _lastStatsBySite = new();
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
    /// Get the most recent stats for all modems at a site
    /// </summary>
    public CellularModemStats? GetLastStats(int siteId)
    {
        _lastStatsBySite.TryGetValue(siteId, out var stats);
        return stats;
    }

    /// <summary>
    /// Auto-discover U5G-Max modems from UniFi device list
    /// </summary>
    public async Task<List<DiscoveredModem>> DiscoverModemsAsync(int siteId)
    {
        var discovered = new List<DiscoveredModem>();

        if (!_connectionService.IsConnected(siteId) || _connectionService.GetClient(siteId) == null)
        {
            _logger.LogWarning("Cannot discover modems: UniFi controller not connected");
            return discovered;
        }

        try
        {
            var devices = await _connectionService.GetClient(siteId)!.GetDevicesAsync();

            foreach (var device in devices)
            {
                // Use product database to identify cellular modems
                if (UniFiProductDatabase.IsCellularModem(device.Model, device.Shortname, device.Type))
                {
                    var displayModel = UniFiProductDatabase.GetBestProductName(device.Model, device.Shortname, null);
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
    /// Execute SSH poll to modem via qmicli commands.
    /// Runs signal, serving system, cell location, and band info queries in a single SSH session
    /// (to avoid rate limiting), then delegates parsing to QmicliParser for each section.
    /// </summary>
    private async Task<CellularModemStats?> ExecutePollAsync(int siteId, string host, string name, string qmiDevice)
    {
        _logger.LogInformation("Polling modem {Name} at {Host} for site {SiteId}", name, host, siteId);

        try
        {
            var stats = new CellularModemStats
            {
                ModemHost = host,
                ModemName = name,
                Timestamp = DateTime.UtcNow
            };

            var combinedCommand = $"echo '===SIGNAL===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-signal-info; " +
                                  $"echo '===SERVING===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-serving-system; " +
                                  $"echo '===CELL===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-cell-location-info; " +
                                  $"echo '===BAND===' && qmicli -d {qmiDevice} --device-open-proxy --nas-get-rf-band-info";

            var (success, output) = await _sshService.RunCommandAsync(siteId, host, combinedCommand);

            if (!success)
            {
                _logger.LogWarning("Failed to poll modem {Name}: {Output}", name, output);
                return null;
            }

            var sections = ParseCombinedOutput(output);

            if (sections.TryGetValue("SIGNAL", out var signalOutput))
            {
                var (lte, nr5g) = QmicliParser.ParseSignalInfo(signalOutput);
                stats.Lte = lte;
                stats.Nr5g = nr5g;
            }

            if (sections.TryGetValue("SERVING", out var servingOutput))
            {
                var (regState, carrier, mcc, mnc, roaming) = QmicliParser.ParseServingSystem(servingOutput);
                stats.RegistrationState = regState;
                stats.Carrier = carrier;
                stats.CarrierMcc = mcc;
                stats.CarrierMnc = mnc;
                stats.IsRoaming = roaming;
            }

            if (sections.TryGetValue("CELL", out var cellOutput))
            {
                var (servingCell, neighbors) = QmicliParser.ParseCellLocationInfo(cellOutput);
                stats.ServingCell = servingCell;
                stats.NeighborCells = neighbors;
            }

            if (sections.TryGetValue("BAND", out var bandOutput))
            {
                stats.ActiveBand = QmicliParser.ParseRfBandInfo(bandOutput);
            }

            _lastStatsBySite[siteId] = stats;

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
    /// Test SSH connection to a modem using shared credentials
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(int siteId, string host)
    {
        return await _sshService.TestConnectionAsync(siteId, host);
    }

    /// <summary>
    /// Poll a modem - fetches stats via SSH and updates LastPolled timestamp
    /// </summary>
    public async Task<(bool success, string message)> PollModemAsync(int siteId, ModemConfiguration modem)
    {
        try
        {
            var stats = await ExecutePollAsync(siteId, modem.Host, modem.Name, modem.QmiDevice);

            if (stats != null)
            {
                // Update LastPolled in database
                await UpdateModemConfigAsync(siteId, modem.Id, null);

                _lastStatsBySite[siteId] = stats;

                return (true, $"Modem polled successfully. RSRP: {stats.Lte?.Rsrp ?? stats.Nr5g?.Rsrp}dBm");
            }
            else
            {
                await UpdateModemConfigAsync(siteId, modem.Id, "Poll returned no data");
                return (false, "Failed to poll modem - no data returned");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing modem {Name}", modem.Name);
            await UpdateModemConfigAsync(siteId, modem.Id, ex.Message);
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get all configured modems for a site
    /// </summary>
    public async Task<List<ModemConfiguration>> GetModemsAsync(int siteId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        return await repository.GetModemConfigurationsAsync(siteId);
    }

    /// <summary>
    /// Add or update a modem configuration (simplified - no SSH creds needed)
    /// </summary>
    public async Task<ModemConfiguration> SaveModemAsync(int siteId, ModemConfiguration config)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        await repository.SaveModemConfigurationAsync(siteId, config);
        return config;
    }

    /// <summary>
    /// Delete a modem configuration
    /// </summary>
    public async Task DeleteModemAsync(int siteId, int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
        await repository.DeleteModemConfigurationAsync(siteId, id);

        // Clear cached stats for this site since the modem may have been the one producing them
        _lastStatsBySite.TryRemove(siteId, out _);
    }

    private async Task PollAllModemsAsync()
    {
        if (_isPolling) return;

        try
        {
            _isPolling = true;

            // Only poll configured and enabled modems (not auto-discovered ones)
            // Auto-discovered modems must be added to config before they're polled
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();
            var siteRepository = scope.ServiceProvider.GetRequiredService<ISiteRepository>();

            // Get all sites and poll modems for each
            var sites = await siteRepository.GetAllSitesAsync();

            foreach (var site in sites)
            {
                // Check if SSH is configured for this site
                var sshSettings = await _sshService.GetSettingsAsync(site.Id);
                if (!sshSettings.Enabled || !sshSettings.HasCredentials)
                {
                    continue; // SSH not configured for this site, skip polling
                }

                var modems = await repository.GetEnabledModemConfigurationsAsync(site.Id);

                foreach (var modem in modems)
                {
                    // Check if it's time to poll this modem
                    if (modem.LastPolled.HasValue)
                    {
                        var elapsed = DateTime.UtcNow - modem.LastPolled.Value;
                        if (elapsed.TotalSeconds < modem.PollingIntervalSeconds)
                            continue;
                    }

                    await PollModemAsync(site.Id, modem);
                }
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

    private async Task UpdateModemConfigAsync(int siteId, int modemId, string? error)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IModemRepository>();

            var config = await repository.GetModemConfigurationAsync(siteId, modemId);
            if (config != null)
            {
                config.LastPolled = DateTime.UtcNow;
                config.LastError = error;
                config.UpdatedAt = DateTime.UtcNow;
                await repository.SaveModemConfigurationAsync(siteId, config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update modem config after poll");
        }
    }

    /// <summary>
    /// Parse combined SSH output into sections by marker
    /// </summary>
    private static Dictionary<string, string> ParseCombinedOutput(string output)
    {
        var sections = new Dictionary<string, string>();
        var markers = new[] { "===SIGNAL===", "===SERVING===", "===CELL===", "===BAND===" };
        var keys = new[] { "SIGNAL", "SERVING", "CELL", "BAND" };

        for (int i = 0; i < markers.Length; i++)
        {
            var startIndex = output.IndexOf(markers[i]);
            if (startIndex == -1) continue;

            startIndex += markers[i].Length;

            // Find end (next marker or end of string)
            var endIndex = output.Length;
            for (int j = i + 1; j < markers.Length; j++)
            {
                var nextMarker = output.IndexOf(markers[j], startIndex);
                if (nextMarker != -1)
                {
                    endIndex = nextMarker;
                    break;
                }
            }

            sections[keys[i]] = output.Substring(startIndex, endIndex - startIndex).Trim();
        }

        return sections;
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

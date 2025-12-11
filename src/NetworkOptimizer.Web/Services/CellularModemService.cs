using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for polling cellular modem stats via SSH
/// </summary>
public class CellularModemService : IDisposable
{
    private readonly ILogger<CellularModemService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly CredentialProtectionService _credentialProtection;
    private readonly Timer? _pollingTimer;
    private readonly object _lock = new();
    private CellularModemStats? _lastStats;
    private bool _isPolling;

    public CellularModemService(ILogger<CellularModemService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _credentialProtection = new CredentialProtectionService();

        // Start polling timer (checks every minute, but respects per-modem intervals)
        _pollingTimer = new Timer(PollAllModems, null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
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
    /// Poll a specific modem immediately
    /// </summary>
    public async Task<CellularModemStats?> PollModemAsync(ModemConfiguration config)
    {
        _logger.LogInformation("Polling modem {Name} at {Host}", config.Name, config.Host);

        try
        {
            var stats = new CellularModemStats
            {
                ModemHost = config.Host,
                ModemName = config.Name,
                Timestamp = DateTime.UtcNow
            };

            // Run all qmicli commands
            var signalTask = RunSshCommandAsync(config, $"qmicli -d {config.QmiDevice} --device-open-proxy --nas-get-signal-info");
            var servingTask = RunSshCommandAsync(config, $"qmicli -d {config.QmiDevice} --device-open-proxy --nas-get-serving-system");
            var cellTask = RunSshCommandAsync(config, $"qmicli -d {config.QmiDevice} --device-open-proxy --nas-get-cell-location-info");
            var bandTask = RunSshCommandAsync(config, $"qmicli -d {config.QmiDevice} --device-open-proxy --nas-get-rf-band-info");

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

            // Update config in database
            await UpdateModemConfigAsync(config.Id, null);

            _logger.LogInformation("Successfully polled modem {Name}: {Carrier}, Signal Quality: {Quality}%",
                config.Name, stats.Carrier, stats.SignalQuality);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling modem {Name}", config.Name);
            await UpdateModemConfigAsync(config.Id, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Test SSH connection to a modem
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync(ModemConfiguration config)
    {
        try
        {
            var result = await RunSshCommandAsync(config, "echo 'Connection successful'");
            if (result.success && result.output.Contains("Connection successful"))
            {
                return (true, "SSH connection successful");
            }
            return (false, result.output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Get all configured modems
    /// </summary>
    public async Task<List<ModemConfiguration>> GetModemsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        return await db.ModemConfigurations.ToListAsync();
    }

    /// <summary>
    /// Add or update a modem configuration
    /// </summary>
    public async Task<ModemConfiguration> SaveModemAsync(ModemConfiguration config)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

        config.UpdatedAt = DateTime.UtcNow;

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(config.Password) && !_credentialProtection.IsEncrypted(config.Password))
        {
            config.Password = _credentialProtection.Encrypt(config.Password);
        }

        if (config.Id == 0)
        {
            config.CreatedAt = DateTime.UtcNow;
            db.ModemConfigurations.Add(config);
        }
        else
        {
            db.ModemConfigurations.Update(config);
        }

        await db.SaveChangesAsync();
        return config;
    }

    /// <summary>
    /// Delete a modem configuration
    /// </summary>
    public async Task DeleteModemAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

        var config = await db.ModemConfigurations.FindAsync(id);
        if (config != null)
        {
            db.ModemConfigurations.Remove(config);
            await db.SaveChangesAsync();
        }
    }

    private async void PollAllModems(object? state)
    {
        if (_isPolling) return;

        try
        {
            _isPolling = true;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

            var modems = await db.ModemConfigurations
                .Where(m => m.Enabled)
                .ToListAsync();

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
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

            var config = await db.ModemConfigurations.FindAsync(modemId);
            if (config != null)
            {
                config.LastPolled = DateTime.UtcNow;
                config.LastError = error;
                config.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update modem config after poll");
        }
    }

    private async Task<(bool success, string output)> RunSshCommandAsync(ModemConfiguration config, string command)
    {
        var sshArgs = new List<string>
        {
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "ConnectTimeout=10",
            "-o", "BatchMode=yes",
            "-p", config.Port.ToString()
        };

        // Add authentication
        if (!string.IsNullOrEmpty(config.PrivateKeyPath))
        {
            sshArgs.Add("-i");
            sshArgs.Add(config.PrivateKeyPath);
        }

        sshArgs.Add($"{config.Username}@{config.Host}");
        sshArgs.Add(command);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = string.Join(" ", sshArgs),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // If password auth, we need sshpass (or rely on ssh-agent/key)
        if (!string.IsNullOrEmpty(config.Password) && string.IsNullOrEmpty(config.PrivateKeyPath))
        {
            // Decrypt the password before use
            var decryptedPassword = _credentialProtection.Decrypt(config.Password);
            // Use sshpass for password authentication
            startInfo.FileName = "sshpass";
            startInfo.Arguments = $"-p {decryptedPassword} ssh {string.Join(" ", sshArgs)}";
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var completed = await Task.WhenAny(
                Task.Run(() => process.WaitForExit(30000)),
                Task.Delay(30000)
            );

            if (!process.HasExited)
            {
                process.Kill();
                return (false, "SSH command timed out");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                return (false, string.IsNullOrEmpty(error) ? output : error);
            }

            return (true, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        _pollingTimer?.Dispose();
    }
}

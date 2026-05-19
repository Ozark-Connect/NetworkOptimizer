using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for reading and writing system-wide settings
/// </summary>
public class SystemSettingsService : ISystemSettingsService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemSettingsService> _logger;

    // Default values
    public const int DefaultIperf3Duration = 10;
    public const int DefaultIperf3Port = 5201;

    // Per-device-type defaults
    public const int DefaultIperf3GatewayParallelStreams = 3;
    public const int DefaultIperf3UniFiParallelStreams = 3;
    public const int DefaultIperf3OtherParallelStreams = 10;

    // Cached external speed test server origins for CORS checks (volatile reference swap for thread safety)
    private volatile HashSet<string> _cachedExternalOrigins = new(StringComparer.OrdinalIgnoreCase);

    public SystemSettingsService(IServiceProvider serviceProvider, ILogger<SystemSettingsService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public bool IsExternalSpeedTestOrigin(string origin)
    {
        return !string.IsNullOrEmpty(origin) && _cachedExternalOrigins.Contains(origin);
    }

    public void UpdateCachedExternalOrigins(IEnumerable<ExternalSpeedTestServer>? servers)
    {
        _cachedExternalOrigins = servers == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                servers.Where(s => s.IsConfigured).Select(s => s.Url),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get a setting value by key
    /// </summary>
    public async Task<string?> GetAsync(string key)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        return await repository.GetSystemSettingAsync(key);
    }

    /// <summary>
    /// Get a setting value as int with default
    /// </summary>
    public async Task<int> GetIntAsync(string key, int defaultValue)
    {
        var value = await GetAsync(key);
        if (string.IsNullOrEmpty(value) || !int.TryParse(value, out var result))
            return defaultValue;
        return result;
    }

    /// <summary>
    /// Set a setting value
    /// </summary>
    public async Task SetAsync(string key, string? value)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repository.SaveSystemSettingAsync(key, value);
        var isSensitive = key.Contains("_key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || key.Contains("account_id", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("System setting {Key} updated{Value}", key,
            isSensitive ? "" : $" to {value}");
    }

    /// <summary>
    /// Set a setting value as int
    /// </summary>
    public Task SetIntAsync(string key, int value) => SetAsync(key, value.ToString());

    /// <summary>
    /// Get iperf3 test duration setting
    /// </summary>
    public Task<int> GetIperf3DurationAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3Duration, DefaultIperf3Duration);

    /// <summary>
    /// Set iperf3 test duration setting
    /// </summary>
    public Task SetIperf3DurationAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3Duration, value);

    /// <summary>
    /// Get iperf3 gateway parallel streams setting
    /// </summary>
    public Task<int> GetIperf3GatewayParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3GatewayParallelStreams, DefaultIperf3GatewayParallelStreams);

    /// <summary>
    /// Set iperf3 gateway parallel streams setting
    /// </summary>
    public Task SetIperf3GatewayParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3GatewayParallelStreams, value);

    /// <summary>
    /// Get iperf3 UniFi device parallel streams setting
    /// </summary>
    public Task<int> GetIperf3UniFiParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3UniFiParallelStreams, DefaultIperf3UniFiParallelStreams);

    /// <summary>
    /// Set iperf3 UniFi device parallel streams setting
    /// </summary>
    public Task SetIperf3UniFiParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3UniFiParallelStreams, value);

    /// <summary>
    /// Get iperf3 other device parallel streams setting
    /// </summary>
    public Task<int> GetIperf3OtherParallelStreamsAsync() =>
        GetIntAsync(SystemSettingKeys.Iperf3OtherParallelStreams, DefaultIperf3OtherParallelStreams);

    /// <summary>
    /// Set iperf3 other device parallel streams setting
    /// </summary>
    public Task SetIperf3OtherParallelStreamsAsync(int value) =>
        SetIntAsync(SystemSettingKeys.Iperf3OtherParallelStreams, value);

    /// <summary>
    /// Get all iperf3 settings as a DTO
    /// </summary>
    public async Task<Iperf3Settings> GetIperf3SettingsAsync()
    {
        return new Iperf3Settings
        {
            DurationSeconds = await GetIperf3DurationAsync(),
            GatewayParallelStreams = await GetIperf3GatewayParallelStreamsAsync(),
            UniFiParallelStreams = await GetIperf3UniFiParallelStreamsAsync(),
            OtherParallelStreams = await GetIperf3OtherParallelStreamsAsync()
        };
    }

    /// <summary>
    /// Save all iperf3 settings from a DTO
    /// </summary>
    public async Task SaveIperf3SettingsAsync(Iperf3Settings settings)
    {
        await SetIperf3DurationAsync(settings.DurationSeconds);
        await SetIperf3GatewayParallelStreamsAsync(settings.GatewayParallelStreams);
        await SetIperf3UniFiParallelStreamsAsync(settings.UniFiParallelStreams);
        await SetIperf3OtherParallelStreamsAsync(settings.OtherParallelStreams);
    }

    /// <summary>
    /// Check if local iperf3 is available on this server (runs iperf3 --version)
    /// </summary>
    public async Task<LocalIperf3Status> CheckLocalIperf3Async(bool forceRefresh = false)
    {
        // Check cache first (unless forcing refresh)
        if (!forceRefresh)
        {
            var cachedStatus = await GetCachedLocalIperf3StatusAsync();
            if (cachedStatus != null)
            {
                return cachedStatus;
            }
        }

        // Run iperf3 --version to check availability
        var status = await RunLocalIperf3VersionCheckAsync();

        // Cache the result
        await CacheLocalIperf3StatusAsync(status);

        return status;
    }

    /// <summary>
    /// Get cached local iperf3 status (returns null if cache expired or not set)
    /// </summary>
    public async Task<LocalIperf3Status?> GetCachedLocalIperf3StatusAsync()
    {
        var availableStr = await GetAsync(SystemSettingKeys.Iperf3LocalAvailable);
        var version = await GetAsync(SystemSettingKeys.Iperf3LocalVersion);
        var lastCheckedStr = await GetAsync(SystemSettingKeys.Iperf3LocalLastChecked);

        if (string.IsNullOrEmpty(availableStr) || string.IsNullOrEmpty(lastCheckedStr))
            return null;

        if (!bool.TryParse(availableStr, out var available))
            return null;

        if (!DateTime.TryParse(lastCheckedStr, out var lastChecked))
            return null;

        // Cache expires after 1 hour
        if (DateTime.UtcNow - lastChecked > TimeSpan.FromHours(1))
            return null;

        return new LocalIperf3Status
        {
            IsAvailable = available,
            Version = version,
            LastChecked = lastChecked
        };
    }

    /// <summary>
    /// Cache local iperf3 status
    /// </summary>
    private async Task CacheLocalIperf3StatusAsync(LocalIperf3Status status)
    {
        await SetAsync(SystemSettingKeys.Iperf3LocalAvailable, status.IsAvailable.ToString());
        await SetAsync(SystemSettingKeys.Iperf3LocalVersion, status.Version);
        await SetAsync(SystemSettingKeys.Iperf3LocalLastChecked, status.LastChecked.ToString("O"));
    }

    /// <summary>
    /// Run iperf3 --version locally to check availability
    /// </summary>
    private async Task<LocalIperf3Status> RunLocalIperf3VersionCheckAsync()
    {
        var status = new LocalIperf3Status { LastChecked = DateTime.UtcNow };

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ProcessUtilities.GetIperf3Path(),
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                status.IsAvailable = false;
                status.Error = "Failed to start iperf3 process";
                return status;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            // Wait with timeout
            var completed = process.WaitForExit(5000);
            if (!completed)
            {
                try { process.Kill(); } catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill process"); }
                status.IsAvailable = false;
                status.Error = "iperf3 version check timed out";
                return status;
            }

            if (process.ExitCode == 0 || !string.IsNullOrEmpty(output))
            {
                status.IsAvailable = true;
                // Parse version from output (e.g., "iperf 3.14 (cJSON 1.7.15)")
                var versionLine = output.Split('\n').FirstOrDefault()?.Trim();
                status.Version = versionLine ?? "iperf3";
                _logger.LogInformation("Local iperf3 available: {Version}", status.Version);
            }
            else
            {
                status.IsAvailable = false;
                status.Error = string.IsNullOrEmpty(error) ? "iperf3 not found" : error.Trim();
                _logger.LogWarning("Local iperf3 not available: {Error}", status.Error);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2) // File not found
        {
            status.IsAvailable = false;
            status.Error = "iperf3 not installed or not in PATH";
            _logger.LogWarning("Local iperf3 not found: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            status.IsAvailable = false;
            status.Error = ex.Message;
            _logger.LogWarning("Error checking local iperf3: {Message}", ex.Message);
        }

        return status;
    }

    /// <summary>
    /// Get default external speed test server settings (backward compat for alerts/schedule).
    /// </summary>
    public async Task<ExternalSpeedTestSettings> GetExternalSpeedTestSettingsAsync()
    {
        var server = await GetDefaultExternalSpeedTestServerAsync();
        if (server == null)
            return new ExternalSpeedTestSettings();

        return new ExternalSpeedTestSettings
        {
            Host = server.Host,
            Port = server.Port,
            Scheme = server.Scheme,
            Name = server.Name,
            ServerId = server.ServerId
        };
    }

    #region External Speed Test Server CRUD

    public async Task<List<ExternalSpeedTestServer>> GetExternalSpeedTestServersAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        return await db.ExternalSpeedTestServers
            .OrderByDescending(s => s.IsDefault)
            .ThenBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<ExternalSpeedTestServer?> GetDefaultExternalSpeedTestServerAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        return await db.ExternalSpeedTestServers.FirstOrDefaultAsync(s => s.IsDefault)
            ?? await db.ExternalSpeedTestServers.FirstOrDefaultAsync();
    }

    public async Task<ExternalSpeedTestServer?> GetExternalSpeedTestServerAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        return await db.ExternalSpeedTestServers.FindAsync(id);
    }

    public async Task AddExternalSpeedTestServerAsync(ExternalSpeedTestServer server)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        server.ServerId = ExternalSpeedTestServer.GenerateServerId(server.Name);
        server.CreatedAt = DateTime.UtcNow;

        var anyExist = await db.ExternalSpeedTestServers.AnyAsync();
        if (!anyExist)
            server.IsDefault = true;

        db.ExternalSpeedTestServers.Add(server);
        await db.SaveChangesAsync();
    }

    public async Task UpdateExternalSpeedTestServerAsync(ExternalSpeedTestServer server)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var existing = await db.ExternalSpeedTestServers.FindAsync(server.Id);
        if (existing == null) return;

        existing.Name = server.Name;
        existing.Host = server.Host;
        existing.Port = server.Port;
        existing.Scheme = server.Scheme;
        existing.ServerId = ExternalSpeedTestServer.GenerateServerId(server.Name);

        await db.SaveChangesAsync();
    }

    public async Task DeleteExternalSpeedTestServerAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var server = await db.ExternalSpeedTestServers.FindAsync(id);
        if (server == null) return;

        var wasDefault = server.IsDefault;
        db.ExternalSpeedTestServers.Remove(server);
        await db.SaveChangesAsync();

        if (wasDefault)
        {
            var next = await db.ExternalSpeedTestServers.FirstOrDefaultAsync();
            if (next != null)
            {
                next.IsDefault = true;
                await db.SaveChangesAsync();
            }
        }
    }

    public async Task SetDefaultExternalSpeedTestServerAsync(int id)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();

        var servers = await db.ExternalSpeedTestServers.ToListAsync();
        if (!servers.Any(s => s.Id == id)) return;

        foreach (var s in servers)
            s.IsDefault = s.Id == id;
        await db.SaveChangesAsync();
    }

    #endregion
}

/// <summary>
/// DTO for external speed test server settings
/// </summary>
public class ExternalSpeedTestSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 3005;
    public string Scheme { get; set; } = "https";
    public string Name { get; set; } = "";
    public string ServerId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    public string Url => IsConfigured
        ? (Port == 443 || Port == 80)
            ? $"{Scheme}://{Host}"
            : $"{Scheme}://{Host}:{Port}"
        : "";
}

/// <summary>
/// DTO for iperf3 test settings
/// </summary>
public class Iperf3Settings
{
    public int DurationSeconds { get; set; } = SystemSettingsService.DefaultIperf3Duration;
    public int GatewayParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3GatewayParallelStreams;
    public int UniFiParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3UniFiParallelStreams;
    public int OtherParallelStreams { get; set; } = SystemSettingsService.DefaultIperf3OtherParallelStreams;
}

/// <summary>
/// Status of local iperf3 installation on the server
/// </summary>
public class LocalIperf3Status
{
    public bool IsAvailable { get; set; }
    public string? Version { get; set; }
    public string? Error { get; set; }
    public DateTime LastChecked { get; set; }
}

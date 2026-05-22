using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

public class SnmpDetectionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public bool SnmpEnabled { get; set; }
    public bool SnmpV3Enabled { get; set; }
    public string? Community { get; set; }
    public string? V3Username { get; set; }
    public string? V3Password { get; set; }

    public SnmpDetectionState DetectionState
    {
        get
        {
            if (!Success) return SnmpDetectionState.NotChecked;
            if (!SnmpEnabled && !SnmpV3Enabled) return SnmpDetectionState.Disabled;
            if (SnmpEnabled && !string.IsNullOrEmpty(Community)) return SnmpDetectionState.EnabledV2c;
            if (SnmpV3Enabled) return SnmpDetectionState.EnabledV3Only;
            return SnmpDetectionState.Disabled;
        }
    }
}

public class SnmpDetectionService
{
    private readonly UniFiConnectionService _connectionService;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<SnmpDetectionService> _logger;
    private static readonly SemaphoreSlim _settingsLock = new(1, 1);

    public SnmpDetectionService(
        UniFiConnectionService connectionService,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ICredentialProtectionService credentialProtection,
        ILogger<SnmpDetectionService> logger)
    {
        _connectionService = connectionService;
        _dbFactory = dbFactory;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    public async Task<SnmpDetectionResult> DetectSnmpSettingsAsync(int maxRetries = 3, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var result = await TryDetectOnceAsync(ct);
            if (result.Success)
                return result;

            if (attempt < maxRetries)
            {
                _logger.LogDebug("SNMP detection attempt {Attempt} failed, retrying in {Delay} ms", attempt + 1, (attempt + 1) * 1000);
                await Task.Delay((attempt + 1) * 1000, ct);
            }
            else
            {
                return result;
            }
        }

        return new SnmpDetectionResult { Success = false, ErrorMessage = "Detection failed after retries" };
    }

    private async Task<SnmpDetectionResult> TryDetectOnceAsync(CancellationToken ct)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            return new SnmpDetectionResult
            {
                Success = false,
                ErrorMessage = "Not connected to UniFi Console"
            };
        }

        try
        {
            using var settings = await _connectionService.Client.GetSettingsRawAsync(ct);
            if (settings == null)
            {
                return new SnmpDetectionResult
                {
                    Success = false,
                    ErrorMessage = "Could not read UniFi settings"
                };
            }

            if (!settings.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return new SnmpDetectionResult
                {
                    Success = false,
                    ErrorMessage = "Unexpected settings response format"
                };
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out var key) || key.GetString() != "snmp")
                    continue;

                var result = new SnmpDetectionResult { Success = true };

                if (item.TryGetProperty("enabled", out var enabled))
                    result.SnmpEnabled = enabled.GetBoolean();

                if (item.TryGetProperty("enabledV3", out var enabledV3))
                    result.SnmpV3Enabled = enabledV3.GetBoolean();

                if (item.TryGetProperty("community", out var community))
                    result.Community = community.GetString();

                if (item.TryGetProperty("username", out var username))
                    result.V3Username = username.GetString();

                if (item.TryGetProperty("x_password", out var password))
                    result.V3Password = password.GetString();

                _logger.LogInformation(
                    "SNMP detection: enabled={Enabled}, v3={V3}, community={HasCommunity}, v3user={HasV3User}",
                    result.SnmpEnabled, result.SnmpV3Enabled,
                    !string.IsNullOrEmpty(result.Community),
                    !string.IsNullOrEmpty(result.V3Username));

                return result;
            }

            return new SnmpDetectionResult
            {
                Success = true,
                SnmpEnabled = false,
                SnmpV3Enabled = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect SNMP settings from UniFi API");
            return new SnmpDetectionResult
            {
                Success = false,
                ErrorMessage = $"Failed to read settings: {ex.Message}"
            };
        }
    }

    public async Task<MonitoringSettings> GetOrCreateSettingsAsync(CancellationToken ct = default)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings != null) return settings;

            settings = new MonitoringSettings();
            db.MonitoringSettings.Add(settings);
            await db.SaveChangesAsync(ct);
            return settings;
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task SaveDetectionResultAsync(SnmpDetectionResult result, CancellationToken ct = default)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings == null)
            {
                settings = new MonitoringSettings();
                db.MonitoringSettings.Add(settings);
            }

        settings.SnmpDetectionState = result.DetectionState;
        settings.LastSnmpDetection = DateTime.UtcNow;

        if (result.DetectionState == SnmpDetectionState.EnabledV2c)
        {
            settings.SnmpVersion = SnmpVersionSetting.V2c;
            settings.SnmpCommunity = _credentialProtection.Encrypt(result.Community!);
            if (!string.IsNullOrEmpty(result.V3Username))
            {
                settings.SnmpV3Username = result.V3Username;
                settings.SnmpV3AuthPassword = !string.IsNullOrEmpty(result.V3Password)
                    ? _credentialProtection.Encrypt(result.V3Password)
                    : null;
            }
        }
        else if (result.DetectionState == SnmpDetectionState.EnabledV3Only)
        {
            settings.SnmpVersion = SnmpVersionSetting.V3;
            settings.SnmpV3Username = result.V3Username;
            settings.SnmpV3AuthPassword = !string.IsNullOrEmpty(result.V3Password)
                ? _credentialProtection.Encrypt(result.V3Password)
                : null;
        }

            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _settingsLock.Release();
        }
    }
}

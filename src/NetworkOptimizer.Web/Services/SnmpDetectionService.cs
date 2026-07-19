using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

public class SnmpDetectionResult
{
    /// <summary>
    /// Longest community string UniFi devices reliably accept. UniFi Network lets you
    /// save a longer one at the Console level, but past this length device support gets
    /// firmware-dependent: switches typically silently reject it and drop from polling,
    /// while gateways often (not always) tolerate it and keep reporting. Warn the user
    /// when the detected community exceeds this so they can shorten it.
    /// </summary>
    public const int MaxSupportedCommunityLength = 20;

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public bool SnmpEnabled { get; set; }
    public bool SnmpV3Enabled { get; set; }
    public string? Community { get; set; }
    public string? V3Username { get; set; }
    public string? V3Password { get; set; }

    /// <summary>
    /// True when SNMP v2c is enabled with a community string longer than
    /// <see cref="MaxSupportedCommunityLength"/> - switches typically drop from polling
    /// (the gateway may keep reporting) and nothing heals until it's shortened.
    /// </summary>
    public bool CommunityTooLong =>
        SnmpEnabled && Community is { Length: > MaxSupportedCommunityLength };

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
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ILogger<SnmpDetectionService> _logger;
    private static readonly SemaphoreSlim _settingsLock = new(1, 1);

    public SnmpDetectionService(
        UniFiConnectionService connectionService,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        ICredentialProtectionService credentialProtection,
        ILogger<SnmpDetectionService> logger)
    {
        _connectionService = connectionService;
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _credentialProtection = credentialProtection;
        _logger = logger;
    }

    /// <summary>
    /// Context for the current site's database. Detection runs against the scoped
    /// (current-site) console connection, so its results MUST land in that same
    /// site's MonitoringSettings row. Writing to the main database from a secondary
    /// site's context overwrites the main site's SNMP credentials with the secondary
    /// console's - the main poller then sends every request with the wrong community,
    /// which devices silently drop, and all SNMP collection times out.
    /// </summary>
    private async Task<NetworkOptimizerDbContext> CreateDbAsync(CancellationToken ct)
    {
        if (!_siteContext.IsDefault)
            return _siteDbFactory.CreateForSite(_siteContext.Slug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ct);
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

            var result = ParseSnmpSettings(settings);
            if (result.Success)
            {
                _logger.LogInformation(
                    "SNMP detection: enabled={Enabled}, v3={V3}, community={HasCommunity}, v3user={HasV3User}",
                    result.SnmpEnabled, result.SnmpV3Enabled,
                    !string.IsNullOrEmpty(result.Community),
                    !string.IsNullOrEmpty(result.V3Username));
            }
            return result;
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

    /// <summary>
    /// Parses the SNMP section out of a raw UniFi settings response (the document
    /// returned by <c>GetSettingsRawAsync</c>). Pure and side-effect-free so both the
    /// interactive detection flow and the collection agent's self-heal can share one
    /// parser and never drift. Returns Success=false only when the response shape is
    /// unusable; a well-formed response with SNMP off returns Success=true with the
    /// enabled flags cleared.
    /// </summary>
    public static SnmpDetectionResult ParseSnmpSettings(JsonDocument settings)
    {
        if (settings == null
            || !settings.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
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

            return result;
        }

        return new SnmpDetectionResult
        {
            Success = true,
            SnmpEnabled = false,
            SnmpV3Enabled = false
        };
    }

    /// <summary>
    /// Maps a detection result onto a <see cref="MonitoringSettings"/> entity: version,
    /// community, and v3 credentials, encrypting secrets via <paramref name="credentialProtection"/>.
    /// Sets <see cref="MonitoringSettings.SnmpDetectionState"/> but leaves the timestamp
    /// fields to the caller. Shared by the interactive save path and the collection
    /// agent's self-heal adopt path so credential handling stays identical.
    /// </summary>
    public static void ApplyToSettings(
        MonitoringSettings settings,
        SnmpDetectionResult result,
        ICredentialProtectionService credentialProtection)
    {
        settings.SnmpDetectionState = result.DetectionState;

        // Never store a too-long community: devices reject it, so adopting it clobbers a
        // possibly-working credential for nothing. Record the state/timestamps (the UI
        // warning keys off the detection result, not storage) and keep the old creds so
        // the fixed community registers as a change when the user shortens it.
        if (result.CommunityTooLong) return;

        if (result.DetectionState == SnmpDetectionState.EnabledV2c)
        {
            settings.SnmpVersion = SnmpVersionSetting.V2c;
            settings.SnmpCommunity = credentialProtection.Encrypt(result.Community!);
            if (!string.IsNullOrEmpty(result.V3Username))
            {
                settings.SnmpV3Username = result.V3Username;
                settings.SnmpV3AuthPassword = !string.IsNullOrEmpty(result.V3Password)
                    ? credentialProtection.Encrypt(result.V3Password)
                    : null;
            }
        }
        else if (result.DetectionState == SnmpDetectionState.EnabledV3Only)
        {
            settings.SnmpVersion = SnmpVersionSetting.V3;
            settings.SnmpV3Username = result.V3Username;
            settings.SnmpV3AuthPassword = !string.IsNullOrEmpty(result.V3Password)
                ? credentialProtection.Encrypt(result.V3Password)
                : null;
        }
    }

    /// <summary>
    /// Whether the SNMP config just pulled from the console differs from what a
    /// <see cref="MonitoringSettings"/> row currently holds (version, community, or v3
    /// credentials), including SNMP having been turned off entirely. Decrypts the stored
    /// secrets to compare. Shared by the direct-poll self-heal and the agent-site
    /// re-detect so both decide "did it actually change?" identically.
    /// </summary>
    public static bool ConfigDiffers(
        MonitoringSettings current,
        SnmpDetectionResult detected,
        ICredentialProtectionService credentialProtection)
    {
        switch (detected.DetectionState)
        {
            case SnmpDetectionState.Disabled:
                return current.SnmpDetectionState != SnmpDetectionState.Disabled;

            case SnmpDetectionState.EnabledV2c:
                // Re-enabled after we adopted Disabled counts as a change even when the
                // credentials are identical - otherwise the state parks at Disabled forever.
                if (current.SnmpDetectionState == SnmpDetectionState.Disabled) return true;
                if (current.SnmpVersion != SnmpVersionSetting.V2c) return true;
                var storedCommunity = string.IsNullOrEmpty(current.SnmpCommunity)
                    ? string.Empty
                    : credentialProtection.Decrypt(current.SnmpCommunity);
                return !string.Equals(storedCommunity, detected.Community ?? string.Empty, StringComparison.Ordinal);

            case SnmpDetectionState.EnabledV3Only:
                if (current.SnmpDetectionState == SnmpDetectionState.Disabled) return true;
                if (current.SnmpVersion != SnmpVersionSetting.V3) return true;
                if (!string.Equals(current.SnmpV3Username, detected.V3Username, StringComparison.Ordinal)) return true;
                var storedPassword = string.IsNullOrEmpty(current.SnmpV3AuthPassword)
                    ? string.Empty
                    : credentialProtection.Decrypt(current.SnmpV3AuthPassword);
                return !string.Equals(storedPassword, detected.V3Password ?? string.Empty, StringComparison.Ordinal);

            default:
                return false;
        }
    }

    public async Task<MonitoringSettings> GetOrCreateSettingsAsync(CancellationToken ct = default)
    {
        await _settingsLock.WaitAsync(ct);
        try
        {
            await using var db = await CreateDbAsync(ct);
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
            await using var db = await CreateDbAsync(ct);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings == null)
            {
                settings = new MonitoringSettings();
                db.MonitoringSettings.Add(settings);
            }

            ApplyToSettings(settings, result, _credentialProtection);
            settings.LastSnmpDetection = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _settingsLock.Release();
        }
    }
}

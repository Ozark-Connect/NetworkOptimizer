using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides access to threat-related SystemSettings, implementing the interface
/// defined in the Threats project to avoid circular references with Storage.
///
/// Threat/CTI settings (CrowdSec API key + daily quota) are instance-wide, so this
/// reads and writes the MAIN database via the context factory - NOT a site-scoped
/// context. A scoped context would resolve to the current site's database, so
/// browsing the threat dashboard on a secondary site would read that site's own
/// empty SystemSettings row and report CrowdSec as unconfigured.
/// </summary>
public class ThreatSettingsAccessor : IThreatSettingsAccessor
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ICredentialProtectionService _credentialService;

    public ThreatSettingsAccessor(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ICredentialProtectionService credentialService)
    {
        _mainDbFactory = mainDbFactory;
        _credentialService = credentialService;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.FindAsync([key], cancellationToken);
        return setting?.Value;
    }

    public async Task<string?> GetDecryptedSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = await GetSettingAsync(key, cancellationToken);
        if (value != null && _credentialService.IsEncrypted(value))
            return _credentialService.Decrypt(value);
        return value;
    }

    public async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.FindAsync([key], cancellationToken);
        if (setting != null)
        {
            setting.Value = value;
        }
        else
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

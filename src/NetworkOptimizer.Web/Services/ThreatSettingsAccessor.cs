using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides access to threat-related SystemSettings, implementing the interface
/// defined in the Threats project to avoid circular references with Storage.
/// </summary>
public class ThreatSettingsAccessor : IThreatSettingsAccessor
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ICredentialProtectionService _credentialService;

    public ThreatSettingsAccessor(NetworkOptimizerDbContext context, ICredentialProtectionService credentialService)
    {
        _context = context;
        _credentialService = credentialService;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        var setting = await _context.SystemSettings.FindAsync([key], cancellationToken);
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
        var setting = await _context.SystemSettings.FindAsync([key], cancellationToken);
        if (setting != null)
        {
            setting.Value = value;
        }
        else
        {
            _context.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        await _context.SaveChangesAsync(cancellationToken);
    }
}

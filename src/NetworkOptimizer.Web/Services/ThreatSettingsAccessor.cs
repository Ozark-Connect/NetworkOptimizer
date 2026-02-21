using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides access to threat-related SystemSettings, implementing the interface
/// defined in the Threats project to avoid circular references with Storage.
/// </summary>
public class ThreatSettingsAccessor : IThreatSettingsAccessor
{
    private readonly NetworkOptimizerDbContext _context;

    public ThreatSettingsAccessor(NetworkOptimizerDbContext context)
    {
        _context = context;
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        var setting = await _context.SystemSettings.FindAsync([key], cancellationToken);
        return setting?.Value;
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

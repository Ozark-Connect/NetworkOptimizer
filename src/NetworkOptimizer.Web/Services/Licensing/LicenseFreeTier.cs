using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Licensing;

/// <summary>
/// The personal-use free-tier slots that sit beside license keys. An admin who declares personal,
/// non-commercial use gets <see cref="SiteManagementService.FreeSiteLimit"/> slots that cover
/// sites with no key, so a license only has to cover the sites past the free three.
/// </summary>
/// <param name="PersonalUse">True when the admin has declared personal, non-commercial use.</param>
/// <param name="SiteIds">Site ids on a free-tier slot. Counted only while <paramref name="PersonalUse"/> is true.</param>
/// <param name="Answered">
/// True once anyone has answered personal use either way (the activation question or the checkbox),
/// so the activation question asks once per install, not after every key.
/// </param>
public sealed record LicenseFreeTier(bool PersonalUse, IReadOnlyList<int> SiteIds, bool Answered = false)
{
    /// <summary>No declaration and no slots in use: the state of every install before this feature.</summary>
    public static readonly LicenseFreeTier None = new(false, []);

    /// <summary>The slot site ids the state machine honors: empty unless personal use is declared.</summary>
    public IReadOnlyList<int> EffectiveSiteIds => PersonalUse ? SiteIds : [];

    /// <summary>Reads the declaration and the slot list. A missing or unreadable setting reads as off / empty.</summary>
    public static async Task<LicenseFreeTier> LoadAsync(NetworkOptimizerDbContext db)
    {
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.LicensingPersonalUse || s.Key == SystemSettingKeys.LicensingFreeTierSites)
            .ToListAsync();
        var declaration = rows.FirstOrDefault(r => r.Key == SystemSettingKeys.LicensingPersonalUse)?.Value;
        var personalUse = declaration == "true";
        var json = rows.FirstOrDefault(r => r.Key == SystemSettingKeys.LicensingFreeTierSites)?.Value;
        List<int> ids = [];
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                ids = JsonSerializer.Deserialize<List<int>>(json) ?? [];
            }
            catch (JsonException)
            {
                ids = [];
            }
        }
        return new LicenseFreeTier(personalUse, ids.Distinct().ToList(), declaration != null);
    }

    /// <summary>Writes both settings onto <paramref name="db"/>; the caller saves.</summary>
    public static async Task StageAsync(NetworkOptimizerDbContext db, LicenseFreeTier value, DateTime nowUtc)
    {
        await UpsertAsync(db, SystemSettingKeys.LicensingPersonalUse, value.PersonalUse ? "true" : "false", nowUtc);
        await UpsertAsync(db, SystemSettingKeys.LicensingFreeTierSites, JsonSerializer.Serialize(value.SiteIds), nowUtc);
    }

    private static async Task UpsertAsync(NetworkOptimizerDbContext db, string key, string value, DateTime nowUtc)
    {
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (setting == null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = nowUtc;
        }
    }
}

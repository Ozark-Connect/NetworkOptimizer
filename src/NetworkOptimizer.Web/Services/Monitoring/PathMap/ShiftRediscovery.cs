using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>
/// An early Upstream Path Discovery run that a confirmed path shift asks for. The run, not the shift,
/// decides whether there is anything to review: the review flag means "a candidate set is staged",
/// so only a run that stages one may set it. At most one early run a day per WAN, kept in the site's
/// database so a restart does not reset it.
/// </summary>
public static class ShiftRediscovery
{
    /// <summary>Least time between two early runs on one WAN.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromDays(1);

    /// <summary>The key part for the primary WAN, whose run is the unbound one.</summary>
    public const string PrimaryKey = "primary";

    /// <summary>Asks for an early run on a WAN; the re-discovery tick takes it up within the hour.</summary>
    /// <param name="wanInterface">A WAN context's interface key; null for the primary.</param>
    public static async Task RequestAsync(NetworkOptimizerDbContext db, string? wanInterface, DateTime now, CancellationToken ct = default)
    {
        var key = SystemSettingKeys.UpstreamShiftRediscoveryRequestedPrefix + (wanInterface ?? PrimaryKey);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row?.Value != null) return; // already asked for
        if (row == null) db.SystemSettings.Add(row = new SystemSetting { Key = key });
        row.Value = now.ToString("o", CultureInfo.InvariantCulture);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The WANs whose early run is due now: asked for, and none run within <see cref="Debounce"/>.
    /// A request inside the debounce stays and runs once it has passed.
    /// </summary>
    public static async Task<HashSet<string>> DueAsync(NetworkOptimizerDbContext db, DateTime now, CancellationToken ct = default)
    {
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(SystemSettingKeys.UpstreamShiftRediscoveryRequestedPrefix)
                || s.Key.StartsWith(SystemSettingKeys.UpstreamShiftRediscoveryRanPrefix))
            .ToListAsync(ct);
        var ran = rows.Where(r => r.Key.StartsWith(SystemSettingKeys.UpstreamShiftRediscoveryRanPrefix, StringComparison.Ordinal))
            .ToDictionary(r => r.Key[SystemSettingKeys.UpstreamShiftRediscoveryRanPrefix.Length..], r => Parse(r.Value),
                StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(r => r.Value != null && r.Key.StartsWith(SystemSettingKeys.UpstreamShiftRediscoveryRequestedPrefix, StringComparison.Ordinal))
            .Select(r => r.Key[SystemSettingKeys.UpstreamShiftRediscoveryRequestedPrefix.Length..])
            .Where(wan => !ran.TryGetValue(wan, out var at) || at is not DateTime last || now - last >= Debounce)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records that the WAN's early run started, and clears its request.</summary>
    public static async Task MarkRanAsync(NetworkOptimizerDbContext db, string wan, DateTime now, CancellationToken ct = default)
    {
        var requested = SystemSettingKeys.UpstreamShiftRediscoveryRequestedPrefix + wan;
        var ranKey = SystemSettingKeys.UpstreamShiftRediscoveryRanPrefix + wan;
        var rows = await db.SystemSettings.Where(s => s.Key == requested || s.Key == ranKey).ToListAsync(ct);
        if (rows.FirstOrDefault(r => r.Key == requested) is { } req) req.Value = null;
        var ranRow = rows.FirstOrDefault(r => r.Key == ranKey);
        if (ranRow == null) db.SystemSettings.Add(ranRow = new SystemSetting { Key = ranKey });
        ranRow.Value = now.ToString("o", CultureInfo.InvariantCulture);
        ranRow.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static DateTime? Parse(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
}

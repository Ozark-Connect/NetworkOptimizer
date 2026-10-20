using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.Blocking;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// The settings Automatic Blocking, Trusted Sources, the WAF and WAN Flood Protection keep for a site
/// in the main database, keyed "{key}:{slug}" by their ForSite helpers.
/// </summary>
public static class ThreatSiteSettings
{
    // The key families whose ForSite helpers append ":{slug}". Trusted Sources writes through
    // BlockingSettingKeys.ForSite. Matching the family as well as the suffix keeps other features'
    // keys out, whatever they end with.
    private static readonly string[] Families = ["blocking.", "trusted.", "waf.", "wanflood."];

    /// <summary>True when <paramref name="key"/> is one of <paramref name="slug"/>'s own threat settings.</summary>
    public static bool BelongsTo(string key, string slug) =>
        key.EndsWith(":" + slug, StringComparison.Ordinal)
        && Families.Any(f => key.StartsWith(f, StringComparison.Ordinal));

    /// <summary>
    /// Deletes a removed site's threat settings and takes it out of every other site's Trusted
    /// Sources. A slug is reused by the next site created under the same name, and that site must
    /// not inherit a WAF token, a Cloudflare token or other sites' trust. Returns the rows changed.
    /// </summary>
    public static async Task<int> RemoveForSiteAsync(NetworkOptimizerDbContext main, string slug,
        CancellationToken cancellationToken = default)
    {
        var suffix = ":" + slug;
        var rows = await main.SystemSettings
            .Where(s => s.Key.EndsWith(suffix) || s.Key.StartsWith(TrustedSourcesKeys.Sites))
            .ToListAsync(cancellationToken);

        var changed = 0;
        foreach (var row in rows)
        {
            if (BelongsTo(row.Key, slug))
            {
                main.SystemSettings.Remove(row);
                changed++;
            }
            else if (IsTrustedSitesKey(row.Key) && WithoutSite(row.Value, slug) is { } rewritten)
            {
                row.Value = rewritten;
                row.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
            await main.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private static bool IsTrustedSitesKey(string key) =>
        key == TrustedSourcesKeys.Sites || key.StartsWith(TrustedSourcesKeys.Sites + ":", StringComparison.Ordinal);

    // The list without the slug, in the form TrustedSourcesSettings.SaveSitesAsync writes; null when
    // the list does not name it or cannot be read.
    private static string? WithoutSite(string? json, string slug)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var sites = JsonSerializer.Deserialize<List<string?>>(json);
            if (sites == null || !sites.Contains(slug))
                return null;
            return JsonSerializer.Serialize(sites.Where(s => s != slug).ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

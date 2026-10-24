namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Decides which InfluxDB bucket names a site may not take because another site already owns them.
/// </summary>
public static class InfluxBucketOwnership
{
    /// <summary>Default primary bucket name when the main site has not set one.</summary>
    public const string DefaultPrimaryBase = "network_monitoring";

    /// <summary>Default long-term bucket name when the main site has not set one.</summary>
    public const string DefaultLongtermBase = "network_monitoring_longterm";

    /// <summary>
    /// Bucket names owned by the main site or any site other than <paramref name="currentSlug"/>:
    /// their custom names, or the derived <c>{slug}-{base}</c> defaults where none is set.
    /// <paramref name="allSiteSlugs"/> must be every site on the install, not only the ones the
    /// caller can see, or a hidden site's buckets can be claimed. Comparison is case-insensitive.
    /// </summary>
    /// <param name="mainPrimary">The main site's primary bucket setting, or null/blank for the default.</param>
    /// <param name="mainLongterm">The main site's long-term bucket setting, or null/blank for the default.</param>
    /// <param name="allSiteSlugs">Every site slug on the install.</param>
    /// <param name="currentSlug">The site being edited; its own names are never taken.</param>
    /// <param name="readSiteBuckets">Reads a site's custom names. A throw means the site DB is not
    /// provisioned, and the site contributes only its derived names.</param>
    public static async Task<HashSet<string>> GatherTakenAsync(
        string? mainPrimary,
        string? mainLongterm,
        IEnumerable<string> allSiteSlugs,
        string currentSlug,
        Func<string, Task<(string? Primary, string? Longterm)>> readSiteBuckets)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mainPrimaryBase = DefaultPrimaryBase;
        var mainLongtermBase = DefaultLongtermBase;
        if (!string.IsNullOrWhiteSpace(mainPrimary))
        {
            mainPrimaryBase = mainPrimary.Trim();
            taken.Add(mainPrimaryBase);
        }
        if (!string.IsNullOrWhiteSpace(mainLongterm))
        {
            mainLongtermBase = mainLongterm.Trim();
            taken.Add(mainLongtermBase);
        }

        foreach (var slug in allSiteSlugs)
        {
            if (string.Equals(slug, currentSlug, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(slug, SiteManagementService.DefaultSiteSlug, StringComparison.OrdinalIgnoreCase)) continue;

            string? customPrimary = null, customLongterm = null;
            try
            {
                (customPrimary, customLongterm) = await readSiteBuckets(slug);
            }
            catch { /* an unprovisioned site DB contributes only its derived names */ }

            taken.Add(!string.IsNullOrWhiteSpace(customPrimary) ? customPrimary.Trim() : $"{slug}-{mainPrimaryBase}");
            taken.Add(!string.IsNullOrWhiteSpace(customLongterm) ? customLongterm.Trim() : $"{slug}-{mainLongtermBase}");
        }

        return taken;
    }
}

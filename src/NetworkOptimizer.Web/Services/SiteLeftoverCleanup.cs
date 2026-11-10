using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The per-site stores that live outside a site's data directory, removed with the site: its floor
/// plan images, its report PDFs, and its default-named InfluxDB buckets. Every step is best effort:
/// a failure logs a warning and never fails the removal.
/// </summary>
public sealed class SiteLeftoverCleanup
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ICredentialProtectionService _credentials;
    private readonly IInfluxDbProvisioningService _provisioner;
    private readonly ILogger<SiteLeftoverCleanup> _logger;

    public SiteLeftoverCleanup(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ICredentialProtectionService credentials,
        IInfluxDbProvisioningService provisioner,
        ILogger<SiteLeftoverCleanup> logger)
    {
        _mainDbFactory = mainDbFactory;
        _credentials = credentials;
        _provisioner = provisioner;
        _logger = logger;
    }

    /// <summary>Removes a non-default site's floor plans, report PDFs, and default buckets.</summary>
    public async Task RemoveAsync(string slug, CancellationToken ct = default)
    {
        RemoveDirectory(slug, "floor plan",
            FloorPlanService.GetFloorPlanDirectory(slug, isDefault: true),
            FloorPlanService.GetFloorPlanDirectory(slug, isDefault: false));
        RemoveDirectory(slug, "report PDF",
            PdfStorageService.GetSitePdfDirectory(slug, isDefault: true),
            PdfStorageService.GetSitePdfDirectory(slug, isDefault: false));
        await RemoveDefaultBucketsAsync(slug, ct);
    }

    /// <summary>
    /// Deletes a site's directory under a store's root. Refuses the root itself (the default site's
    /// directory) and anything not strictly inside it, since other sites' directories sit in the root.
    /// </summary>
    internal bool RemoveDirectory(string slug, string store, string root, string siteDirectory)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var fullSite = Path.GetFullPath(siteDirectory);
            var within = Path.GetRelativePath(fullRoot, fullSite);
            if (within == "." || within == ".." || Path.IsPathRooted(within)
                || within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || within.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                _logger.LogWarning("Site {Slug} was removed but its {Store} directory was not inside the {Store} root; left in place",
                    slug, store, store);
                return false;
            }

            if (!Directory.Exists(fullSite)) return false;
            Directory.Delete(fullSite, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Site {Slug} was removed but its {Store} directory could not be deleted", slug, store);
            return false;
        }
    }

    /// <summary>
    /// The bucket names a site falls back to when it names none, as the InfluxDB client resolves
    /// them. A custom name is never listed: it may be shared with another site or other tooling.
    /// Main's own bucket names are never listed either.
    /// </summary>
    internal static IReadOnlyList<string> DefaultBucketNames(string slug, MonitoringSettings main)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(main.InfluxDbBucket))
            names.Add($"{slug}-{main.InfluxDbBucket}");
        if (!string.IsNullOrWhiteSpace(main.InfluxDbLongtermBucket))
            names.Add($"{slug}-{main.InfluxDbLongtermBucket}");

        return names
            .Where(n => !string.Equals(n, main.InfluxDbBucket?.Trim(), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(n, main.InfluxDbLongtermBucket?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task RemoveDefaultBucketsAsync(string slug, CancellationToken ct)
    {
        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(ct);
            var main = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (main == null
                || string.IsNullOrWhiteSpace(main.InfluxDbUrl)
                || string.IsNullOrWhiteSpace(main.InfluxDbOrg)
                || string.IsNullOrWhiteSpace(main.InfluxDbToken)
                || string.IsNullOrWhiteSpace(main.InfluxDbBucket))
                return;

            var names = DefaultBucketNames(slug, main);
            if (names.Count == 0) return;

            var url = main.InfluxDbUrl.Trim();
            var token = _credentials.Decrypt(main.InfluxDbToken);
            var orgId = await _provisioner.TryResolveOrgIdAsync(url, token, main.InfluxDbOrg.Trim(), ct);
            if (string.IsNullOrEmpty(orgId))
            {
                _logger.LogWarning(
                    "Site {Slug} was removed but its InfluxDB buckets {Buckets} were left in place: the shared token cannot manage buckets",
                    slug, string.Join(", ", names));
                return;
            }

            var deleted = await _provisioner.DeleteBucketsAsync(url, token, orgId, names, ct);
            if (deleted.Count > 0)
                _logger.LogInformation("Deleted InfluxDB buckets {Buckets} of removed site {Slug}", string.Join(", ", deleted), slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Site {Slug} was removed but its InfluxDB buckets could not be deleted", slug);
        }
    }
}

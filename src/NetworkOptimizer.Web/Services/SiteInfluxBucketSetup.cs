using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The current site's InfluxDB buckets for AP Telemetry, which runs whether or not anyone opened
/// Monitoring. <see cref="EnsureAsync"/> is the Monitoring wizard's silent step: it creates the
/// site's buckets only when the shared token can already create them. It never prompts, never
/// changes the shared token, and never throws.
/// </summary>
public sealed class SiteInfluxBucketSetup
{
    private readonly SiteContextService _siteContext;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ICredentialProtectionService _credentials;
    private readonly ISiteInfluxProvisioningService _provisioner;
    private readonly MonitoringInfluxRegistry _registry;
    private readonly ILogger<SiteInfluxBucketSetup> _logger;

    public SiteInfluxBucketSetup(
        SiteContextService siteContext,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ICredentialProtectionService credentials,
        ISiteInfluxProvisioningService provisioner,
        MonitoringInfluxRegistry registry,
        ILogger<SiteInfluxBucketSetup> logger)
    {
        _siteContext = siteContext;
        _mainDbFactory = mainDbFactory;
        _credentials = credentials;
        _provisioner = provisioner;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>True when the site's buckets answer a query. Reads only, so any viewer may ask.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            return (await _registry.GetFor(_siteContext.Slug).CheckHealthAsync(ct)).Reachable;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "InfluxDB health check for site {Slug} failed", _siteContext.Slug);
            return false;
        }
    }

    /// <summary>
    /// Creates the site's buckets when they are missing and the shared token allows it, then
    /// reports <see cref="IsReadyAsync"/>. Creating buckets needs Site Admin on the site. The main
    /// site's buckets are the shared connection itself, so they stay with the wizard.
    /// </summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        if (await IsReadyAsync(ct)) return true;
        if (_siteContext.IsDefault) return false;

        var slug = _siteContext.Slug;
        try
        {
            await using var mainDb = await _mainDbFactory.CreateDbContextAsync(ct);
            var shared = await mainDb.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (shared == null
                || string.IsNullOrWhiteSpace(shared.InfluxDbUrl)
                || string.IsNullOrWhiteSpace(shared.InfluxDbOrg)
                || string.IsNullOrWhiteSpace(shared.InfluxDbToken))
                return false;

            // The client's names, so the buckets created are the ones it reads and writes.
            var client = _registry.GetFor(slug);
            if (!client.IsConfigured && !await client.ReconfigureAsync(ct)) return false;
            if (string.IsNullOrEmpty(client.PrimaryBucket) || string.IsNullOrEmpty(client.LongtermBucket)) return false;

            var url = shared.InfluxDbUrl.Trim();
            var token = _credentials.Decrypt(shared.InfluxDbToken);
            // Null when the shared token is main's original two-bucket token. Upgrading it is an
            // installation-wide decision the wizard asks a person for, so stop here.
            var orgId = await _provisioner.TryResolveOrgIdAsync(url, token, shared.InfluxDbOrg.Trim(), ct);
            if (string.IsNullOrEmpty(orgId)) return false;

            var result = await _provisioner.EnsureBucketsAsync(url, token, orgId, client.PrimaryBucket, client.LongtermBucket, ct);
            if (result.CreatedBucketNames.Count > 0)
                _logger.LogInformation("Created InfluxDB buckets {Buckets} for site {Slug} for AP Telemetry",
                    string.Join(", ", result.CreatedBucketNames), slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create InfluxDB buckets for site {Slug}", slug);
            return false;
        }
        return await IsReadyAsync(ct);
    }
}

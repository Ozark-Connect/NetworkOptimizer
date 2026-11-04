namespace NetworkOptimizer.Web.Services;

/// <summary>Threat blocking's view of each site's zone, from <see cref="ISiteTimeZones"/>.</summary>
public sealed class ThreatSiteTimeZoneSource : NetworkOptimizer.Threats.Blocking.ISiteTimeZoneSource
{
    private readonly ISiteTimeZones _zones;

    public ThreatSiteTimeZoneSource(ISiteTimeZones zones) => _zones = zones;

    /// <inheritdoc />
    public TimeZoneInfo? ZoneFor(string? siteSlug) =>
        _zones.Cached(string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug);
}

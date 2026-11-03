using NetworkOptimizer.Alerts.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The notification clock: the site's console zone, else the install default an admin chose, else
/// the server's. Alert readers are often not users (a channel, a mailing list), so the site's own
/// hours are the clock that needs no viewer. The default fills in for a site whose console cannot
/// give a zone, and is read through <see cref="ITimeZonePreferenceSource.GetInheritedAsync"/>, so a
/// host that decorates that seam changes notifications too. Singleton, like the alert pipeline.
/// </summary>
public sealed class NotificationTimeZoneResolver : INotificationTimeZoneResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISiteTimeZones _siteZones;
    private readonly ILogger<NotificationTimeZoneResolver> _logger;

    public NotificationTimeZoneResolver(
        IServiceScopeFactory scopeFactory,
        ISiteTimeZones siteZones,
        ILogger<NotificationTimeZoneResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _siteZones = siteZones;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TimeZoneInfo> ResolveAsync(string? siteSlug, CancellationToken cancellationToken = default)
    {
        try
        {
            var slug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
            if (await _siteZones.GetAsync(slug, cancellationToken) is { } siteZone) return siteZone;

            using var scope = _scopeFactory.CreateScope();
            var inherited = await scope.ServiceProvider.GetRequiredService<ITimeZonePreferenceSource>().GetInheritedAsync();
            return TimeZoneCatalog.TryResolve(inherited, out var installDefault) ? installDefault : TimeZoneInfo.Local;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve the notification time zone for site {Site}; using the server's", siteSlug);
            return TimeZoneInfo.Local;
        }
    }
}

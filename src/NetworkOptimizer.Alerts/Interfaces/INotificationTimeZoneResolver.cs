namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// The clock for text written with no one viewing it: delivered alerts, digests, and the stored
/// reports and notices the web layer writes in the background. The alert pipeline lives below the
/// web layer and cannot read settings or the console, so it depends on this seam; the web layer
/// implements it, and a host may decorate it to put its own zone first.
/// </summary>
public interface INotificationTimeZoneResolver
{
    /// <summary>
    /// The zone for text about <paramref name="siteSlug"/>. A null or empty slug is the default
    /// (main) site. Never throws; falls back to the server's zone.
    /// </summary>
    Task<TimeZoneInfo> ResolveAsync(string? siteSlug, CancellationToken cancellationToken = default);
}

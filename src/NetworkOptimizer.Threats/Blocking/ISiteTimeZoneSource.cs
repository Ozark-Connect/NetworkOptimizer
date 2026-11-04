namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// A site's own time zone, for schedules that mean "at the site". The web layer implements it from
/// the site's UniFi Console; without it, the server's zone is used.
/// </summary>
public interface ISiteTimeZoneSource
{
    /// <summary>The site's zone, or null when it is not known yet. Never waits on the console.</summary>
    /// <param name="siteSlug">The site; null or empty is the default site.</param>
    TimeZoneInfo? ZoneFor(string? siteSlug);
}

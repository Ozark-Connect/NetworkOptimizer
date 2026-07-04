namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// Lets the alert processor pin its DI scope to a specific site's database before it
/// resolves the alert repository, so an alert that originated at a given site is
/// evaluated against that site's rules and delivered to that site's channels. The
/// alert pipeline lives below the web layer and can't reference its site-context
/// service directly, so it depends on this seam; the web layer implements it on the
/// scoped site context.
/// </summary>
public interface IAlertSiteScope
{
    /// <summary>
    /// Pin this scope to the given site. A null or empty slug means the default (main)
    /// site. Must be called before anything in the scope resolves the DbContext.
    /// </summary>
    void UseSite(string? siteSlug);
}

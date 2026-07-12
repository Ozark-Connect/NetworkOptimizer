using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Shared site-switch navigation for the interactive UI. An explicit switch writes
/// the site cookie (the browser-wide default for URLs without a ?site= selector)
/// and reloads with ?site= stamped on the target, so the switching tab is pinned to
/// the chosen site while other tabs keep theirs.
/// </summary>
public class SiteSwitchService
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigation;
    private readonly SiteContextService _siteContext;
    private readonly SiteManagementService _siteManagement;

    public SiteSwitchService(
        IJSRuntime js,
        NavigationManager navigation,
        SiteContextService siteContext,
        SiteManagementService siteManagement)
    {
        _js = js;
        _navigation = navigation;
        _siteContext = siteContext;
        _siteManagement = siteManagement;
    }

    /// <summary>
    /// Makes the given site this browser's default and reloads the tab onto it.
    /// Site context is scoped per circuit, so a full reload is required for every
    /// scoped service to resolve against the new site's database.
    /// </summary>
    /// <param name="slug">Slug of the site to switch to.</param>
    /// <param name="targetUrl">
    /// Page to land on, defaulting to the current URL. Any #fragment is dropped:
    /// reloading the *same* URL with its fragment is a same-document navigation the
    /// browser won't actually reload, and the fragment is meaningless on another site.
    /// </param>
    public async Task SwitchToAsync(string slug, string? targetUrl = null)
    {
        await _js.InvokeVoidAsync("eval",
            $"document.cookie = '{SiteContextService.CookieName}={slug}; path=/; max-age=31536000; SameSite=Lax'");

        var target = targetUrl ?? _navigation.Uri;
        var fragmentAt = target.IndexOf('#');
        if (fragmentAt >= 0)
            target = target[..fragmentAt];

        _navigation.NavigateTo(SiteContextService.WithSiteParam(target, slug), forceLoad: true);
    }

    /// <summary>
    /// Stamps the current site onto a full-page navigation target so a pinned tab
    /// keeps its site across the reload. Returns the URL unchanged when multi-site
    /// is disabled, keeping single-site URLs clean.
    /// </summary>
    public async Task<string> StampSiteAsync(string url) =>
        await _siteManagement.IsMultiSiteEnabledAsync()
            ? SiteContextService.WithSiteParam(url, _siteContext.Slug)
            : url;
}

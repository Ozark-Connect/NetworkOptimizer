using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using NetworkOptimizer.Storage.Models.Identity;

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
    private readonly AuthenticationStateProvider _authState;
    private readonly Authorization.IEffectiveSiteRoleResolver _siteRoles;

    public SiteSwitchService(
        IJSRuntime js,
        NavigationManager navigation,
        SiteContextService siteContext,
        SiteManagementService siteManagement,
        AuthenticationStateProvider authState,
        Authorization.IEffectiveSiteRoleResolver siteRoles)
    {
        _js = js;
        _navigation = navigation;
        _siteContext = siteContext;
        _siteManagement = siteManagement;
        _authState = authState;
        _siteRoles = siteRoles;
    }

    /// <summary>
    /// Makes the given site this browser's default and reloads the tab onto it.
    /// Site context is scoped per circuit, so a full reload is required for every
    /// scoped service to resolve against the new site's database.
    /// </summary>
    /// <param name="slug">Slug of the site to switch to.</param>
    /// <param name="targetUrl">
    /// Page to land on, defaulting to the current URL. Any #fragment is preserved so
    /// switching sites from an anchored spot (e.g. a highlighted setting) lands on the
    /// same spot on the new site; the changed ?site= query guarantees the full reload.
    /// </param>
    public async Task SwitchToAsync(string slug, string? targetUrl = null)
    {
        await _js.InvokeVoidAsync("eval",
            $"document.cookie = '{SiteContextService.CookieName}={slug}; path=/; max-age=31536000; SameSite=Lax'");

        var target = targetUrl ?? _navigation.Uri;

        // Settings only opens on a site you administer, so switching from it to a site you do not
        // would land on a refusal rather than the site you asked for. Nobody switches sites in order
        // to be told no - send them to the new site's dashboard instead.
        if (targetUrl is null && await RefusesOnTargetSiteAsync(target, slug))
            target = _navigation.BaseUri;

        _navigation.NavigateTo(SiteContextService.WithSiteParam(target, slug), forceLoad: true);
    }

    /// <summary>
    /// Whether the page being carried across the switch is one the caller cannot open on the site
    /// they are switching to. An install with authentication off has no principal and never refuses;
    /// a global Admin administers every site, so this is false for them too.
    /// </summary>
    private async Task<bool> RefusesOnTargetSiteAsync(string url, string slug)
    {
        if (!new Uri(url).AbsolutePath.StartsWith("/settings", StringComparison.OrdinalIgnoreCase))
            return false;

        var user = (await _authState.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true)
            return false;

        return await _siteRoles.GetEffectiveRoleAsync(user, slug) != SiteRole.SiteAdmin;
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

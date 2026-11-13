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
    /// Page to land on, defaulting to the current URL without its site-bound parameters
    /// (<see cref="StripSiteBoundParams"/>). Any #fragment is preserved so
    /// switching sites from an anchored spot (e.g. a highlighted setting) lands on the
    /// same spot on the new site; the changed ?site= query guarantees the full reload.
    /// </param>
    public async Task SwitchToAsync(string slug, string? targetUrl = null)
    {
        await _js.InvokeVoidAsync("eval",
            $"document.cookie = '{SiteContextService.CookieName}={slug}; path=/; max-age=31536000; SameSite=Lax'");

        // A caller's own targetUrl is built for the new site, so it keeps whatever it carries.
        var target = targetUrl ?? StripSiteBoundParams(_navigation.Uri);

        // Settings only opens on a site you administer, so switching from it to a site you do not
        // would land on a refusal rather than the site you asked for. Nobody switches sites in order
        // to be told no - send them to the new site's dashboard instead.
        if (targetUrl is null && await RefusesOnTargetSiteAsync(target, slug))
            target = _navigation.BaseUri;

        _navigation.NavigateTo(SiteContextService.WithSiteParam(target, slug), forceLoad: true);
    }

    // Parameters that name something on one site: a WAN, a device, a client, a target ID, a DB row.
    // Carried across a switch they point at nothing, or at an unrelated row with the same ID.
    // Keyed by page because names collide: /network-tools ?target= is a host, which every site shares.
    private static readonly string[] SiteBoundEverywhere = ["wan"];

    private static readonly System.Text.RegularExpressions.Regex PlanPath =
        new("^/change-control/plans/[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string[]> SiteBoundByPath = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/monitoring"] = ["target", "device", "client", "sfp", "modem", "cm", "ont", "starlink", "mifip"],
        ["/network-tools"] = ["from"],
        ["/wifi-optimizer"] = ["client", "ap", "edge"],
        ["/client-dashboard"] = ["ip"],
        ["/alerts"] = ["alert"],
        ["/speedtest"] = ["result"],
        ["/wan-speedtest"] = ["result"],
        ["/client-speedtest"] = ["result"],
        // Live Traffic: a client and a resolver are this site's; an app, a protocol, and a port mean the
        // same anywhere. Routes and destinations are judged per value (SiteBoundValuesByPath).
        ["/monitoring/live-traffic"] = ["client", "not-client", "resolver", "not-resolver"],
    };

    /// <summary>
    /// Per page, parameters whose site-boundness depends on the value: a Live Traffic route named for this
    /// site's WAN or VPN goes but LAN, which every site has, stays; a destination naming a LAN device (a
    /// private address or a UniFi name) goes but an internet name or address stays.
    /// </summary>
    private static readonly Dictionary<string, (string[] Names, Func<string, bool> IsSiteBound)[]> SiteBoundValuesByPath = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/monitoring/live-traffic"] =
        [
            (["route", "not-route"], v => !string.Equals(v, "LAN", StringComparison.OrdinalIgnoreCase)),
            (["dest", "not-dest"], v => !IsInternetDestination(v)),
        ],
    };

    /// <summary>
    /// Returns the URL without the query parameters that only mean something on the site being left,
    /// so the page lands in its default view on the new site instead of on a missing device or WAN.
    /// Time ranges, tabs, categories, and external hosts or IPs are kept.
    /// </summary>
    /// <param name="url">Absolute URL of the page being carried across the switch.</param>
    public static string StripSiteBoundParams(string url)
    {
        var path = new Uri(url).AbsolutePath.TrimEnd('/');
        // A plan's ID is a row in the old site's database: on the new site it is another plan or none.
        if (PlanPath.IsMatch(path)) return new Uri(new Uri(url), "/change-control").ToString();
        var names = SiteBoundByPath.TryGetValue(path, out var forPage)
            ? SiteBoundEverywhere.Concat(forPage)
            : SiteBoundEverywhere;
        foreach (var name in names)
            url = SiteContextService.RemoveQueryParam(url, name);
        if (SiteBoundValuesByPath.TryGetValue(path, out var byValue))
            url = RemoveSiteBoundValues(url, byValue);
        return url;
    }

    /// <summary>
    /// A Live Traffic destination every site shares: a public address, or a DNS name (dotted, no spaces).
    /// Anything else names this site: a private address, or a client or UniFi device by its UniFi name
    /// ("[AP] Living Room"). An owner name ("Example Org") is dropped too, which costs nothing much.
    /// </summary>
    private static bool IsInternetDestination(string value)
    {
        if (System.Net.IPAddress.TryParse(value, out var ip)) return !NetworkOptimizer.Core.Helpers.NetworkUtilities.IsPrivateIpAddress(ip);
        return value.Contains('.') && !value.Any(char.IsWhiteSpace) && !value.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops each listed parameter's values its rule calls site-bound, keeping its other values.</summary>
    private static string RemoveSiteBoundValues(string url, (string[] Names, Func<string, bool> IsSiteBound)[] rules)
    {
        var uri = new Uri(url);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var changed = false;
        var parts = new List<string>();
        foreach (var (key, values) in query)
        {
            var rule = rules.FirstOrDefault(r => r.Names.Contains(key, StringComparer.OrdinalIgnoreCase));
            foreach (var value in values)
            {
                if (value != null && rule.IsSiteBound != null && rule.IsSiteBound(value))
                {
                    changed = true;
                    continue;
                }
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value ?? "")}");
            }
        }
        if (!changed) return url;
        // AbsoluteUri, not ToString: ToString unescapes, and a WAN name's %20 would come back a bare space.
        return new UriBuilder(uri) { Query = string.Join("&", parts) }.Uri.AbsoluteUri;
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

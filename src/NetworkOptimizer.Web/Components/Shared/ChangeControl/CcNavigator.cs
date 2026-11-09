using Microsoft.AspNetCore.Components;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>
/// Navigation from the multi-site views into one site's Change Control pages. A different site
/// needs a full site switch, since site context is per circuit.
/// </summary>
public sealed class CcNavigator
{
    private readonly NavigationManager _nav;
    private readonly SiteContextService _site;
    private readonly SiteSwitchService _switch;

    public CcNavigator(NavigationManager nav, SiteContextService site, SiteSwitchService siteSwitch)
    {
        _nav = nav;
        _site = site;
        _switch = siteSwitch;
    }

    public Task GoAsync(string slug, string url)
    {
        if (string.Equals(slug, _site.Slug, StringComparison.OrdinalIgnoreCase))
        {
            _nav.NavigateTo(SiteContextService.WithSiteParam(url, slug));
            return Task.CompletedTask;
        }
        return _switch.SwitchToAsync(slug, url);
    }

    public Task TabAsync(string slug, string tab, string? query = null) =>
        GoAsync(slug, $"/change-control/{tab}" + (query == null ? "" : "?" + query));

    public Task ChangeAsync(string slug, int changeId) => TabAsync(slug, "changes", $"change={changeId}");

    public Task RollBackChangeAsync(string slug, int changeId) => TabAsync(slug, "changes", $"change={changeId}&rollback=1");

    public Task PlanAsync(string slug, int planId) => GoAsync(slug, $"/change-control/plans/{planId}");

    public Task CompareAsync(string slug, int? from, int? to) =>
        TabAsync(slug, "snapshots", $"from={from?.ToString() ?? "now"}&to={to?.ToString() ?? "now"}");
}

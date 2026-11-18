using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The address bar as a page last wrote it with <c>history.replaceState</c> behind Blazor's router
/// (Live Traffic's filters: a real navigation would re-run the router and scroll to the heading on
/// every click). <see cref="NavigationManager.Uri"/> never sees those writes, so anything that builds
/// a link from the current URL (the site switcher) asks here instead. Any real navigation clears it.
/// </summary>
public sealed class AddressBarState : IDisposable
{
    private readonly NavigationManager _navigation;
    private string? _written;

    public AddressBarState(NavigationManager navigation)
    {
        _navigation = navigation;
        _navigation.LocationChanged += OnLocationChanged;
    }

    /// <summary>Records a URL the page wrote straight to the address bar (absolute or base-relative).</summary>
    public void Wrote(string url) => _written = _navigation.ToAbsoluteUri(url).ToString();

    /// <summary>The address bar as it stands: the URL last written behind the router, else Blazor's own.</summary>
    public string Current => _written ?? _navigation.Uri;

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => _written = null;

    public void Dispose() => _navigation.LocationChanged -= OnLocationChanged;
}

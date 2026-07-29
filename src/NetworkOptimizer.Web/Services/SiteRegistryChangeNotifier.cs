namespace NetworkOptimizer.Web.Services;

/// <summary>
/// App-wide broadcast for changes to what sites are on offer: the registry itself (create, rename,
/// enable/disable, remove, multi-site toggle), raised by SiteManagementService, and a change to who
/// may reach one, raised by IdentityAdminService - being granted a site changes the list just as
/// surely as the site appearing does. Live UI that renders the site list (the site switcher in every
/// open circuit) subscribes and rebuilds its OWN access-filtered list, so a broadcast is enough. A singleton because the publisher and subscribers live in different
/// circuits/scopes. Handlers are invoked on the publisher's thread - subscribers
/// must marshal to their own dispatcher (InvokeAsync) before touching state.
/// </summary>
public class SiteRegistryChangeNotifier
{
    public event Action? SitesChanged;

    public void NotifySitesChanged() => SitesChanged?.Invoke();
}

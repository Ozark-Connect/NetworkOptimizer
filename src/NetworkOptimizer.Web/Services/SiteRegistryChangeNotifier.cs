namespace NetworkOptimizer.Web.Services;

/// <summary>
/// App-wide broadcast for site registry changes (create, rename, enable/disable,
/// remove, multi-site toggle). SiteManagementService raises it; live UI that
/// renders the site list (the site switcher in every open circuit) subscribes and
/// rebuilds. A singleton because the publisher and subscribers live in different
/// circuits/scopes. Handlers are invoked on the publisher's thread - subscribers
/// must marshal to their own dispatcher (InvokeAsync) before touching state.
/// </summary>
public class SiteRegistryChangeNotifier
{
    public event Action? SitesChanged;

    public void NotifySitesChanged() => SitesChanged?.Invoke();
}

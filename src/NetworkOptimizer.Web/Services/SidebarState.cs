namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped request channel for the side menu, which MainLayout owns. The menu is off-canvas on a
/// phone and in kiosk mode, so anything that needs it on screen (the tour, spotlighting a nav item)
/// asks here rather than touching the layout's state.
/// </summary>
public class SidebarState
{
    public event Action<bool>? OpenRequested;

    public void Set(bool open) => OpenRequested?.Invoke(open);
}

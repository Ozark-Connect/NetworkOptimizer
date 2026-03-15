namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped service that lets pages register a refresh callback for pull-to-refresh.
/// When a page sets <see cref="RefreshCallback"/>, pull-to-refresh calls it instead of doing a full page reload.
/// </summary>
public class PullToRefreshState
{
    private Func<Task>? _refreshCallback;
    private int _navigationGeneration;
    private int _callbackGeneration;

    public Func<Task>? RefreshCallback
    {
        get => _callbackGeneration >= _navigationGeneration ? _refreshCallback : null;
        set { _refreshCallback = value; _callbackGeneration = _navigationGeneration; }
    }

    public Action? NotifyStateChanged { get; set; }

    /// <summary>
    /// Called by layout on navigation. Increments the generation so stale callbacks
    /// from previous pages are ignored unless the new page re-registers.
    /// </summary>
    public void OnNavigated()
    {
        _navigationGeneration++;
        NotifyStateChanged = null;
    }
}

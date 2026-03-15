namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped service that lets pages register a refresh callback for pull-to-refresh.
/// When a page sets <see cref="RefreshCallback"/>, pull-to-refresh calls it instead of doing a full page reload.
/// </summary>
public class PullToRefreshState
{
    public Func<Task>? RefreshCallback { get; set; }
    public Action? NotifyStateChanged { get; set; }
}

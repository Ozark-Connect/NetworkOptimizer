using NetworkOptimizer.Web.Services.ChangeControl;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>
/// Refreshes a Change Control tab when a site it shows records a snapshot or change: once per burst, a
/// moment after the last one, on the component's own dispatcher. Dispose with the component.
/// </summary>
internal sealed class CcLiveRefresh : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly ChangeControlRegistry _registry;
    private readonly Func<string, bool> _wants;
    private readonly Func<Func<Task>, Task> _invoke;
    private readonly Func<Task> _refresh;
    private readonly ILogger _logger;
    private int _queued;
    private volatile bool _disposed;

    /// <param name="registry">The source of change events.</param>
    /// <param name="wants">Whether a change on this site slug concerns the tab right now.</param>
    /// <param name="invoke">The component's InvokeAsync.</param>
    /// <param name="refresh">Reloads the tab in place.</param>
    /// <param name="logger">Logs a failed refresh.</param>
    public CcLiveRefresh(ChangeControlRegistry registry, Func<string, bool> wants, Func<Func<Task>, Task> invoke, Func<Task> refresh, ILogger logger)
    {
        _registry = registry;
        _wants = wants;
        _invoke = invoke;
        _refresh = refresh;
        _logger = logger;
        _registry.Changed += OnChanged;
    }

    private void OnChanged(string slug)
    {
        if (_disposed || !_wants(slug) || Interlocked.Exchange(ref _queued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(Settle);
            Interlocked.Exchange(ref _queued, 0);
            if (_disposed) return;
            try { await _invoke(_refresh); }
            catch (Exception ex) { _logger.LogDebug(ex, "Change Control live refresh failed"); }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _registry.Changed -= OnChanged;
    }
}

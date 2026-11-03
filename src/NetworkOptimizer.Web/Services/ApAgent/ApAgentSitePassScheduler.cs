namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Runs each site's supervision pass on its own, so a large site mid-rollout never holds up health
/// checks and restarts on the others. One pass per site at a time, and at most
/// <c>maxConcurrentSites</c> sites at once so a multi-site server does not open SSH to every site's
/// fleet together.
/// </summary>
internal sealed class ApAgentSitePassScheduler : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a scheduler that runs up to <paramref name="maxConcurrentSites"/> passes at once.</summary>
    public ApAgentSitePassScheduler(int maxConcurrentSites, ILogger logger)
    {
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrentSites));
        _logger = logger;
    }

    /// <summary>
    /// Starts a pass for the site unless one is already running or waiting for a slot. Returns
    /// whether it started. A pass that throws is logged and frees the site for the next tick.
    /// </summary>
    public bool TryStart(string site, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_running.ContainsKey(site)) return false;
            // The removal in RunAsync takes this lock, so it cannot run before the entry exists.
            _running[site] = Task.Run(() => RunAsync(site, pass, ct), CancellationToken.None);
            return true;
        }
    }

    /// <summary>Whether a pass for the site is running or waiting for a slot.</summary>
    public bool IsRunning(string site)
    {
        lock (_lock) return _running.ContainsKey(site);
    }

    /// <summary>Completes when every pass started so far has finished.</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock) return Task.WhenAll(_running.Values.ToList());
    }

    private async Task RunAsync(string site, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct);
            try { await pass(ct); }
            finally { _slots.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AP Agent supervision pass failed for site {Site}", site);
        }
        finally
        {
            lock (_lock) _running.Remove(site);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _slots.Dispose();
}

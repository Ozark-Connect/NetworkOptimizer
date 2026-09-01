using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Process-wide state behind <see cref="IGatewayAgentInstallService"/>: the latest run per
/// site and the short-lived availability verdicts. A singleton so a run keeps streaming and
/// finishing after the circuit that started it is gone; the gated service itself stays scoped
/// because audit-detail enrichment rides the caller's scope.
/// </summary>
public class GatewayAgentInstallState
{
    private readonly ConcurrentDictionary<string, GatewayAgentInstallRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, bool Available)> _availability = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _pendingChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _startLock = new();

    /// <summary>How long an availability verdict is trusted before the gateway is dialed again.</summary>
    public static readonly TimeSpan AvailabilityCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>The site's latest run (running or finished), or null when none this process.</summary>
    public GatewayAgentInstallRun? GetRun(string siteSlug) =>
        _runs.TryGetValue(siteSlug, out var run) ? run : null;

    /// <summary>
    /// Registers a new run for the site, refusing while one is still running - a second start
    /// is rejected, not queued.
    /// </summary>
    /// <exception cref="InvalidOperationException">A run is already active for the site.</exception>
    public GatewayAgentInstallRun StartRun(string siteSlug, bool isUpgrade, string refusalMessage)
    {
        lock (_startLock)
        {
            if (_runs.TryGetValue(siteSlug, out var existing) && existing.Status == GatewayAgentInstallStatus.Running)
                throw new InvalidOperationException(refusalMessage);

            var run = new GatewayAgentInstallRun(siteSlug, isUpgrade);
            _runs[siteSlug] = run;
            return run;
        }
    }

    /// <summary>
    /// The cached availability verdict for a site, when one is fresh enough to trust.
    /// </summary>
    public bool TryGetAvailability(string siteSlug, out bool available)
    {
        available = false;
        if (!_availability.TryGetValue(siteSlug, out var cached) || DateTime.UtcNow - cached.At >= AvailabilityCacheTtl)
            return false;
        available = cached.Available;
        return true;
    }

    /// <summary>
    /// Runs one availability check per site at a time: concurrent callers (the install panel
    /// and an upgrade row on the same page) share a single dial, and the verdict is cached.
    /// </summary>
    public Task<bool> GetOrStartAvailabilityCheck(string siteSlug, Func<Task<bool>> check)
    {
        return _pendingChecks.GetOrAdd(siteSlug, _ => new Lazy<Task<bool>>(RunAsync)).Value;

        async Task<bool> RunAsync()
        {
            try
            {
                var available = await check();
                _availability[siteSlug] = (DateTime.UtcNow, available);
                return available;
            }
            finally
            {
                _pendingChecks.TryRemove(siteSlug, out _);
            }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Computes whether deployed modules (Performance Tweaks boot scripts, WAN Steering
/// binary) are out of date versus the embedded copies, once per application startup
/// after the UniFi Console connects, and caches the result. Backs the app-wide update
/// banner so the SSH-bound status checks run sparingly rather than on every page load.
/// </summary>
public sealed class ModuleUpdateNotificationService : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UniFiConnectionService _connection;
    private readonly ILogger<ModuleUpdateNotificationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _computed;

    /// <summary>Raised when the cached update state changes so consumers can re-render.</summary>
    public event Action? OnStateChanged;

    /// <summary>True when one or more deployed Performance Tweaks boot scripts are out of date.</summary>
    public bool PerfTweaksUpdateAvailable { get; private set; }

    /// <summary>True when the deployed WAN Steering binary is older than the embedded version.</summary>
    public bool WanSteerUpdateAvailable { get; private set; }

    // TODO: Adaptive SQM update detection. SqmDeploymentService has no deployed-vs-embedded
    // version/hash comparison yet. The SQM scripts have been stable, so we are deferring the
    // versioning work to avoid opening that can of worms. Once SqmDeploymentService tracks a
    // version, add an AdaptiveSqmUpdateAvailable check here and surface it in the banner
    // alongside Performance Tweaks and WAN Steering.

    /// <summary>True when any tracked module has an update available.</summary>
    public bool AnyUpdateAvailable => PerfTweaksUpdateAvailable || WanSteerUpdateAvailable;

    public ModuleUpdateNotificationService(
        IServiceScopeFactory scopeFactory,
        UniFiConnectionService connection,
        ILogger<ModuleUpdateNotificationService> logger)
    {
        _scopeFactory = scopeFactory;
        _connection = connection;
        _logger = logger;
        _connection.OnConnectionChanged += HandleConnectionChanged;
        // If the Console is already connected when this singleton is first resolved,
        // compute now; otherwise the connection event will trigger it.
        if (_connection.IsConnected)
            _ = ComputeOnceAsync();
    }

    private void HandleConnectionChanged()
    {
        if (_connection.IsConnected && !_computed)
            _ = ComputeOnceAsync();
    }

    /// <summary>
    /// Runs the update checks once per application lifetime, after the Console is
    /// connected. No-ops if already computed or if the Console is not connected.
    /// </summary>
    public async Task ComputeOnceAsync()
    {
        if (_computed || !_connection.IsConnected)
            return;

        await _gate.WaitAsync();
        try
        {
            if (_computed || !_connection.IsConnected)
                return;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var perf = scope.ServiceProvider.GetRequiredService<PerfTweaksDeploymentService>();
            var wan = scope.ServiceProvider.GetRequiredService<WanSteerDeploymentService>();

            var perfStatus = await perf.CheckAllStatusAsync();
            PerfTweaksUpdateAvailable = perfStatus.Tweaks.Values.Any(t => t.ScriptOutdated);

            var wanStatus = await wan.GetStatusAsync();
            WanSteerUpdateAvailable = WanSteerDeploymentService.IsBinaryOutdated(wanStatus);

            _computed = true;
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Non-critical: leave _computed false so a later connection event retries.
            _logger.LogDebug(ex, "Module update check failed; will retry on next Console connect.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Updates the cached Performance Tweaks state from a freshly fetched status (e.g. a page
    /// refreshed after a deploy). Reuses the caller's status to avoid a redundant SSH round-trip
    /// and notifies consumers only on an actual change, so the banner dismisses promptly once a
    /// tweak is redeployed. Callers should pass a successfully-read status (Error == null).
    /// </summary>
    public void NotifyPerfTweaksStatus(PerfTweaksStatus status)
    {
        var available = status.Tweaks.Values.Any(t => t.ScriptOutdated);
        if (available == PerfTweaksUpdateAvailable)
            return;
        PerfTweaksUpdateAvailable = available;
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Updates the cached WAN Steering state from a freshly fetched status. See
    /// <see cref="NotifyPerfTweaksStatus"/>. Callers should pass a status whose binary was
    /// actually read (BinaryDeployed) so a transient SSH failure doesn't clear the banner.
    /// </summary>
    public void NotifyWanSteerStatus(WanSteerStatus status)
    {
        var available = WanSteerDeploymentService.IsBinaryOutdated(status);
        if (available == WanSteerUpdateAvailable)
            return;
        WanSteerUpdateAvailable = available;
        OnStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _connection.OnConnectionChanged -= HandleConnectionChanged;
        _gate.Dispose();
    }
}

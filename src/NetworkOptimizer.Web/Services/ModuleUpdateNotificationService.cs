namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Whether deployed modules (Performance Tweaks boot scripts, WAN Steering binary) are out of date
/// versus the embedded copies, for the site in context. Backs the app-wide update banner. A view
/// over the site's shared <see cref="SiteModuleUpdateState"/>; <see cref="ModuleUpdateRegistry"/>
/// decides when the SSH-bound checks run, so rendering a page never triggers one.
/// </summary>
public sealed class ModuleUpdateNotificationService
{
    private readonly SiteModuleUpdateState _state;

    /// <summary>Raised when the cached update state changes so consumers can re-render.</summary>
    public event Action? OnStateChanged
    {
        add => _state.OnStateChanged += value;
        remove => _state.OnStateChanged -= value;
    }

    /// <summary>True when one or more deployed Performance Tweaks boot scripts are out of date.</summary>
    public bool PerfTweaksUpdateAvailable => _state.PerfTweaksUpdateAvailable;

    /// <summary>True when the deployed WAN Steering binary is older than the embedded version.</summary>
    public bool WanSteerUpdateAvailable => _state.WanSteerUpdateAvailable;

    // TODO: Adaptive SQM update detection. SqmDeploymentService has no deployed-vs-embedded
    // version/hash comparison yet. The SQM scripts have been stable, so we are deferring the
    // versioning work to avoid opening that can of worms. Once SqmDeploymentService tracks a
    // version, add an AdaptiveSqmUpdateAvailable check here and surface it in the banner
    // alongside Performance Tweaks and WAN Steering.

    /// <summary>True when any tracked module has an update available.</summary>
    public bool AnyUpdateAvailable => PerfTweaksUpdateAvailable || WanSteerUpdateAvailable;

    public ModuleUpdateNotificationService(ModuleUpdateRegistry registry, SiteContextService siteContext)
    {
        _state = registry.GetFor(siteContext.Slug);
    }

    /// <summary>
    /// Updates the cached Performance Tweaks state from a freshly fetched status (e.g. a page
    /// refreshed after a deploy). Reuses the caller's status to avoid a redundant SSH round-trip
    /// and notifies consumers only on an actual change, so the banner dismisses promptly once a
    /// tweak is redeployed. Callers should pass a successfully-read status (Error == null).
    /// </summary>
    public void NotifyPerfTweaksStatus(PerfTweaksStatus status) => _state.NotifyPerfTweaksStatus(status);

    /// <summary>
    /// Updates the cached WAN Steering state from a freshly fetched status. See
    /// <see cref="NotifyPerfTweaksStatus"/>. Callers should pass a status whose binary was
    /// actually read (BinaryDeployed) so a transient SSH failure doesn't clear the banner.
    /// </summary>
    public void NotifyWanSteerStatus(WanSteerStatus status) => _state.NotifyWanSteerStatus(status);
}

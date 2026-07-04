using NetworkOptimizer.Alerts.Events;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Per-site wrapper around the singleton <see cref="IAlertEventBus"/> that stamps the
/// originating site slug onto every published event that hasn't already set one, so
/// <c>AlertProcessingService</c> can evaluate it against that site's rules and deliver to
/// that site's channels plus the global (main-site) channels. The default site stamps
/// nothing (the processor treats a null slug as the default site). Consume delegates to
/// the inner bus unchanged - only publishers use this wrapper.
/// </summary>
public sealed class SiteAlertEventBus : IAlertEventBus
{
    private readonly IAlertEventBus _inner;
    private readonly string? _siteSlug;

    public SiteAlertEventBus(IAlertEventBus inner, string siteSlug)
    {
        _inner = inner;
        _siteSlug = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? null
            : siteSlug;
    }

    public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
        => _inner.PublishAsync(
            _siteSlug != null && alertEvent.SiteSlug == null
                ? alertEvent with { SiteSlug = _siteSlug }
                : alertEvent,
            cancellationToken);

    public IAsyncEnumerable<AlertEvent> ConsumeAsync(CancellationToken cancellationToken)
        => _inner.ConsumeAsync(cancellationToken);
}

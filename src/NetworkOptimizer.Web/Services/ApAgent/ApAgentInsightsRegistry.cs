using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Per-site roam and radio-health collectors.
///
/// Both hold in-memory state that must never be shared across sites: the roam assembler's view of
/// where each client last associated, and the wedge detector's consecutive-window counts. Same
/// ownership pattern as <see cref="ApAgentTelemetryRegistry"/>; instances hold no unmanaged
/// resources, so nothing needs disposal.
/// </summary>
public sealed class ApAgentInsightsRegistry : ISiteScopedRegistry
{
    /// <summary>One site's collectors.</summary>
    /// <param name="Roams">Reads the event rings and persists roam records.</param>
    /// <param name="RadioHealth">Differences the radio counters and alerts on the wedge.</param>
    public sealed record SiteApAgentInsights(
        ApAgentRoamCollector Roams,
        ApAgentRadioHealthCollector RadioHealth);

    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SiteApAgentInsights> _instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the registry.</summary>
    public ApAgentInsightsRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The collectors for a site, created on first use.</summary>
    public SiteApAgentInsights GetFor(string slug) =>
        _instances.GetOrAdd(slug, s => new SiteApAgentInsights(
            ActivatorUtilities.CreateInstance<ApAgentRoamCollector>(_serviceProvider, s),
            ActivatorUtilities.CreateInstance<ApAgentRadioHealthCollector>(_serviceProvider, s)));

    /// <summary>The default site's collectors.</summary>
    public SiteApAgentInsights GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _instances.TryRemove(slug, out _);
        return null;
    }
}

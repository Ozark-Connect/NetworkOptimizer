using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Per-site AP Agent telemetry collectors. Same pattern as MonitoringLiveStatsRegistry: one
/// instance per site, created on first use. The instances hold in-memory folds and coverage claims
/// only, so the registry never needs to dispose them.
/// </summary>
public sealed class ApAgentTelemetryRegistry : ISiteScopedRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ApAgentTelemetryCollector> _instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the registry.</summary>
    public ApAgentTelemetryRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The telemetry collector for a site, created on first use.</summary>
    public ApAgentTelemetryCollector GetFor(string slug) =>
        _instances.GetOrAdd(slug, s => ActivatorUtilities.CreateInstance<ApAgentTelemetryCollector>(_serviceProvider, s));

    /// <summary>The default site's collector.</summary>
    public ApAgentTelemetryCollector GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _instances.TryRemove(slug, out _);
        return null;
    }
}

using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Core.Interfaces;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Serves the agent presence verdict for the site in context, out of the telemetry collector's
/// membership ledger. Scoped like <see cref="InfluxMeasuredClientSource"/>: the slug is captured
/// at construction so background scopes pinned to a site answer for that site.
/// </summary>
public sealed class ApAgentClientPresenceSource : IAgentClientPresenceSource
{
    private readonly ApAgentTelemetryRegistry _telemetryRegistry;
    private readonly string _siteSlug;

    /// <summary>Creates the source for the site in context.</summary>
    public ApAgentClientPresenceSource(ApAgentTelemetryRegistry telemetryRegistry, SiteContextService siteContext)
    {
        _telemetryRegistry = telemetryRegistry;
        _siteSlug = siteContext.Slug;
    }

    /// <inheritdoc />
    public AgentClientPresence PresenceFor(string? apMac, string? clientMac)
        => _telemetryRegistry.GetFor(_siteSlug).PresenceFor(apMac, clientMac);
}

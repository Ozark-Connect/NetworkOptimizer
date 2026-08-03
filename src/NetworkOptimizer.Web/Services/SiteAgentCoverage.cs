using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Whether a site's on-site agent does the collecting instead of this server.
///
/// For a secondary site that is simply what having an agent means: the server cannot reach the
/// site's network, so the agent probes, polls SNMP and proxies the console. The default site is the
/// server's own network, so its agents have always been ADDITIONAL vantage points rather than
/// replacements - and that is shipped behavior for anyone using one that way today.
///
/// This flag is what lets the default site opt into the secondary-site arrangement, for the case
/// Settings has long described: the server runs off-site and the network being monitored is
/// somewhere else. It is deliberately explicit rather than inferred from an agent being enrolled -
/// inferring it would silently stop server-side collection for every install already using a
/// default-site agent as an extra vantage.
///
/// Nothing here decides anything on its own. Every gate combines it with an agent actually being
/// enrolled (<see cref="AgentProbeService.HasAgentForSite"/>), so a flag set on a site with no
/// agent changes nothing.
/// </summary>
public class SiteAgentCoverage
{
    /// <summary>Per-site setting key: this site's agent collects, this server stands down.</summary>
    public const string AgentCoversSiteKey = "site.agent_covers_collection";

    // Consulted on collection paths that run every few seconds, so cache it briefly rather than
    // hitting SQLite each time - same treatment as the via-agent routing flag.
    private static readonly TimeSpan FlagCacheExpiry = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, (bool Enabled, DateTime At)> _flags = new();

    public SiteAgentCoverage(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>Whether the site is configured for its agent to cover collection.</summary>
    public async Task<bool> CoversAsync(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (_flags.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.At < FlagCacheExpiry)
            return cached.Enabled;
        return await ReadAsync(slug);
    }

    /// <summary>
    /// The cached answer, for the callers that cannot await - the probe executor factory resolves
    /// a vantage from a synchronous property. A cache miss reads false and refreshes in the
    /// background, so the worst case is one pass of today's behavior before the flag takes hold.
    /// </summary>
    public bool Covers(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (_flags.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.At < FlagCacheExpiry)
            return cached.Enabled;
        _ = Task.Run(() => ReadAsync(slug));
        return false;
    }

    /// <summary>Drops the cached answer for a site, so the next read sees a change immediately.</summary>
    public void Invalidate(string slug) => _flags.TryRemove(slug, out _);

    /// <summary>
    /// The question every gate actually asks: does the agent do this site's work rather than this
    /// server? A secondary site needs only an agent, which is what having one has always meant
    /// there. The default site needs the flag as well, so an agent enrolled as an extra vantage
    /// keeps behaving exactly as it does today.
    ///
    /// Defined once because it is asked from eight places, and a copy that drifts is a site that
    /// half stands down.
    /// </summary>
    public bool AgentCovers(string slug, bool agentPresent)
        => agentPresent && (slug != SiteManagementService.DefaultSiteSlug || Covers(slug));

    /// <inheritdoc cref="AgentCovers"/>
    public async Task<bool> AgentCoversAsync(string slug, bool agentPresent)
        => agentPresent && (slug != SiteManagementService.DefaultSiteSlug || await CoversAsync(slug));

    private async Task<bool> ReadAsync(string slug)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(AgentCoversSiteKey);
            var enabled = bool.TryParse(setting?.Value, out var value) && value;
            _flags[slug] = (enabled, DateTime.UtcNow);
            return enabled;
        }
        catch
        {
            // A site mid-removal or mid-creation reads as "server keeps collecting", which is the
            // behavior every install has today.
            return false;
        }
    }
}

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
public class SiteAgentCoverage : ISiteScopedRegistry
{
    /// <summary>Per-site setting key: this site's agent collects, this server stands down.</summary>
    public const string AgentCoversSiteKey = "site.agent_covers_collection";

    // Consulted on collection paths that run every few seconds, so it is cached rather than hitting
    // SQLite each time. Deliberately WITHOUT an expiry: every writer calls Invalidate, so a timed
    // expiry bought nothing and cost a cold-miss window in which the synchronous reader below
    // answers "not covered" for a site that is. On an off-site server that window means probes run
    // from the wrong network and device dials go to RFC1918 addresses on the hosting provider's
    // network instead of through the tunnel. The cache is warmed at startup for the same reason.
    // The one thing this gives up is noticing a value changed in the database behind the app's
    // back, which only an operator editing SQLite directly can do, and a restart settles that.
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, bool> _flags = new();

    public SiteAgentCoverage(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>Whether the site is configured for its agent to cover collection.</summary>
    public async Task<bool> CoversAsync(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (_flags.TryGetValue(slug, out var cached)) return cached;
        return await ReadAsync(slug);
    }

    /// <summary>
    /// The cached answer, for the callers that cannot await - the probe executor factory resolves a
    /// vantage from a synchronous property. The cache is warmed at startup and never expires, so a
    /// miss here means a site created since startup, which has no flag set anyway. It still kicks a
    /// read so the answer is right from the next pass.
    /// </summary>
    public bool Covers(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (_flags.TryGetValue(slug, out var cached)) return cached;
        _ = Task.Run(() => ReadAsync(slug));
        return false;
    }

    /// <summary>Drops the cached answer for a site, so the next read sees a change immediately.</summary>
    public void Invalidate(string slug) => _flags.TryRemove(slug, out _);

    /// <summary>
    /// Swept with the per-site registries when a site is removed or created. The cached answer now
    /// outlives the site that set it - there is no expiry to heal it - so a slug deleted and
    /// re-created would otherwise inherit the previous site's coverage until the next restart.
    /// Nothing to tear down: the entry is a bool.
    /// </summary>
    public Func<ValueTask>? EvictSite(string slug)
    {
        Invalidate(slug);
        return null;
    }

    /// <summary>
    /// Reads every site's flag once at startup. Without this the first pass of any synchronous
    /// caller answers "not covered" while the cache fills, and on an off-site server that pass
    /// probes from the wrong network and dials site addresses directly.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        try
        {
            List<string> slugs;
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
                slugs = db.Sites.Select(x => x.Slug).ToList();
            }
            foreach (var slug in slugs)
            {
                if (ct.IsCancellationRequested) return;
                await ReadAsync(slug);
            }
        }
        catch
        {
            // Best effort: a failure here leaves the old lazy behavior, not a broken start.
        }
    }

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

    /// <summary>
    /// Whether the site's agent owns PATH measurement for this site - latency and loss probes, and
    /// upstream traceroutes. Configuration alone, deliberately without asking whether the agent is
    /// connected right now.
    ///
    /// A probe measures the path FROM whoever runs it. If this server runs one for a site its agent
    /// covers, the result describes this server's route rather than the site's, and it is stored
    /// under the site's name either way. On an off-site server that is a different network
    /// entirely. A probe that does not run leaves a gap; a probe run from the wrong place leaves a
    /// wrong number that looks exactly like data - so this stands down on the configuration and
    /// lets the probe fail while the agent is away.
    ///
    /// Contrast <see cref="AgentCovers"/>, which is the right question for reading device counters:
    /// SNMP returns the device's own numbers whoever asks, so the server continuing while the agent
    /// is down is a genuine fallback rather than a different measurement.
    /// </summary>
    public bool AgentOwnsPathMeasurement(string slug)
        => slug != SiteManagementService.DefaultSiteSlug || Covers(slug);

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
            _flags[slug] = enabled;
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

using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>One selectable vantage for an agent-run WAN speed test: the default path, or a WAN context.</summary>
/// <param name="ContextId">Null for the default path, which has no context row.</param>
/// <param name="Label">Display name for the selector.</param>
/// <param name="Runnable">Whether a test started on this vantage right now would run.</param>
/// <param name="Reason">Why it would not, when <paramref name="Runnable"/> is false.</param>
public sealed record AgentWanVantage(int? ContextId, string Label, bool Runnable, string? Reason);

/// <summary>The agent an agent-run WAN speed test will execute on, and the WAN it measures.</summary>
/// <param name="AgentId">Agent to dispatch the run to.</param>
/// <param name="Context">The chosen WAN context, or null when the run takes the default path.</param>
public sealed record AgentWanTestVantage(int AgentId, WanContext? Context);

/// <summary>
/// Chooses which of a site's agents runs its WAN speed test, and which WAN that measures.
///
/// A site can have several agents - one per WAN behind separate uplinks, or a bare-metal agent for
/// speed testing alongside a gateway agent for monitoring. Picking whichever answered first sends
/// the run to an arbitrary box and, on a mixed site, to one that cannot run it at all.
/// </summary>
public class AgentWanTestVantageResolver
{
    /// <summary>
    /// The unpinned option, named the way the target list already names it in Latency Targets, so
    /// one WAN choice does not read as two different things across the app.
    /// </summary>
    public const string DefaultPathLabel = "Default path";

    private readonly AgentTunnelRegistry _tunnels;
    private readonly AgentOnGatewayDetector _onGateway;
    private readonly AgentProbeResultSink _probeSink;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<AgentWanTestVantageResolver> _logger;

    public AgentWanTestVantageResolver(
        AgentTunnelRegistry tunnels,
        AgentOnGatewayDetector onGateway,
        AgentProbeResultSink probeSink,
        SiteDbContextFactory siteDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<AgentWanTestVantageResolver> logger)
    {
        _tunnels = tunnels;
        _onGateway = onGateway;
        _probeSink = probeSink;
        _siteDbFactory = siteDbFactory;
        _mainDbFactory = mainDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Whether an agent can run the WAN test, which today means it is not on the gateway: the
    /// on-gateway installer ships no uwnspeedtest ("no speed-test machinery"), and the agent
    /// resolves the binary next to itself.
    /// <para>
    /// A proxy for a capability the agent does not report. Not <c>ServesSpeedTest</c> from the
    /// hello - that is the LAN speed test page, which is opt-in at install while uwnspeedtest is
    /// always fetched, so a no there says nothing about this. When gateway installs gain the
    /// binary, replace this with a reported capability rather than widening the proxy.
    /// </para>
    /// </summary>
    public async Task<bool> CanRunAsync(string siteSlug, int agentId, CancellationToken ct = default)
        => !await IsOnGatewayAsync(siteSlug, agentId, ct);

    /// <summary>Whether any connected agent for the site could run a WAN speed test.</summary>
    public async Task<bool> HasCapableAgentAsync(string siteSlug, CancellationToken ct = default)
        => await FirstCapableAgentAsync(siteSlug, ct) != null;

    /// <summary>
    /// The vantages offered on the site's WAN speed test surfaces: the default path, then one entry
    /// per WAN context that could run a test. Empty when that leaves nothing to choose between,
    /// which covers a site with no contexts and a site whose contexts are all served by a gateway
    /// agent - one possibility is not a choice, so no selector is drawn at all.
    /// <para>
    /// Ordered the way Latency Targets orders the same list: by WAN index so it reads WAN1, WAN2,
    /// ... rather than alphabetically, with ties broken by name.
    /// </para>
    /// <para>
    /// The filter is about what CAN run, never about what happens to be up. A context whose agent
    /// is merely offline stays listed and carries its reason, because it comes back; one bound to a
    /// gateway agent or to no agent at all is dropped, because no amount of waiting makes it
    /// runnable and the Gateway test already covers that WAN.
    /// </para>
    /// </summary>
    public async Task<List<AgentWanVantage>> ListVantagesAsync(string siteSlug, CancellationToken ct = default)
    {
        var contexts = await LoadContextsAsync(siteSlug, ct);
        if (contexts.Count == 0) return new List<AgentWanVantage>();

        var capable = new HashSet<int>();
        foreach (var agentId in contexts.Where(c => c.AgentId != null).Select(c => c.AgentId!.Value).Distinct())
            if (await CanRunAsync(siteSlug, agentId, ct))
                capable.Add(agentId);

        var runnable = SelectableContexts(contexts, capable).ToList();
        if (runnable.Count == 0) return new List<AgentWanVantage>();

        var vantages = new List<AgentWanVantage>();
        var (_, defaultPathRefusal) = await ResolveAsync(siteSlug, null, ct);
        vantages.Add(new AgentWanVantage(null, DefaultPathLabel, defaultPathRefusal == null, defaultPathRefusal));

        foreach (var context in runnable)
        {
            var (_, refusal) = await ResolveAsync(siteSlug, context.Id, ct);
            vantages.Add(new AgentWanVantage(context.Id, context.Name, refusal == null, refusal));
        }
        return vantages;
    }

    /// <summary>
    /// The contexts a WAN speed test could be pointed at, in display order. Drops what can never
    /// run - a context with no agent, or one whose agent is on the gateway - rather than listing a
    /// WAN that will refuse every time it is picked. An agent that is simply offline is NOT dropped:
    /// it comes back, and its entry carries the reason meanwhile.
    /// </summary>
    internal static IEnumerable<WanContext> SelectableContexts(
        IEnumerable<WanContext> contexts, IReadOnlyCollection<int> capableAgentIds) =>
        OrderForDisplay(contexts.Where(c => c.AgentId != null && capableAgentIds.Contains(c.AgentId.Value)));

    /// <summary>
    /// WAN contexts in the order every selector shows them: by WAN index, then by name. Shared with
    /// <see cref="Components.Shared.LatencyTargetsCard"/>'s ordering so the two lists match.
    /// </summary>
    internal static IEnumerable<WanContext> OrderForDisplay(IEnumerable<WanContext> contexts) =>
        contexts
            .OrderBy(c => NetworkOptimizer.UniFi.GatewayWanHelper.WanIndexFromKey(c.WanInterface) is var i && i >= 1
                ? i
                : int.MaxValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The agent that runs the test, or the reason none will. Exactly one of the two is non-null.
    /// <para>
    /// A named context is honored or refused, never redirected: running on a different agent would
    /// measure a different WAN and file the number under the name the caller chose, which is worse
    /// than returning nothing. Same rule <see cref="Monitoring.ProbeExecutorFactory.ForAgent"/>
    /// holds for probes.
    /// </para>
    /// </summary>
    /// <param name="siteSlug">Site whose agents are candidates.</param>
    /// <param name="wanContextId">Chosen WAN context, or null to take the site's default path.</param>
    public async Task<(AgentWanTestVantage? Vantage, string? Refusal)> ResolveAsync(
        string siteSlug, int? wanContextId, CancellationToken ct = default)
    {
        var connected = _tunnels.GetForSite(siteSlug).Select(c => c.AgentId).ToList();
        var capable = new HashSet<int>();
        foreach (var agentId in connected)
            if (await CanRunAsync(siteSlug, agentId, ct))
                capable.Add(agentId);

        var contexts = wanContextId == null ? new List<WanContext>() : await LoadContextsAsync(siteSlug, ct);
        var collector = wanContextId == null ? await _probeSink.GetCollectorAgentIdAsync(siteSlug, ct) : null;

        return Decide(wanContextId, contexts, connected, capable, collector);
    }

    /// <summary>
    /// Picks the agent, or says why none will run. Pure, so the rules can be tested without a
    /// tunnel, a console or a database - the same reason
    /// <see cref="AgentProbeResultSink.SelectCollectorAgentId"/> is shaped this way.
    /// <para>
    /// On the default path: the collector when it can run the test, otherwise the lowest-id
    /// connected agent that can. The two rules diverge on purpose - a gateway agent is deliberately
    /// eligible to collect, because it binds each probe to a WAN and one of them can serve a whole
    /// site, and is equally deliberately unable to run this test. On a site pairing a gateway agent
    /// with a bare-metal one, that fallback is what finds the box that can.
    /// </para>
    /// </summary>
    /// <param name="wanContextId">Chosen context, or null for the default path.</param>
    /// <param name="contexts">The site's WAN contexts. Only read when one was chosen.</param>
    /// <param name="connectedAgentIds">Agents with an open tunnel.</param>
    /// <param name="capableAgentIds">Of those, the ones that can run the test.</param>
    /// <param name="collectorAgentId">The site's collector, or null when none owns it.</param>
    internal static (AgentWanTestVantage? Vantage, string? Refusal) Decide(
        int? wanContextId,
        IReadOnlyCollection<WanContext> contexts,
        IReadOnlyCollection<int> connectedAgentIds,
        IReadOnlyCollection<int> capableAgentIds,
        int? collectorAgentId)
    {
        if (wanContextId == null)
        {
            if (collectorAgentId != null && capableAgentIds.Contains(collectorAgentId.Value))
                return (new AgentWanTestVantage(collectorAgentId.Value, null), null);

            var fallback = connectedAgentIds.Where(capableAgentIds.Contains).OrderBy(id => id).ToList();
            return fallback.Count == 0
                ? (null, "No connected agent at this site can run a WAN speed test.")
                : (new AgentWanTestVantage(fallback[0], null), null);
        }

        var context = contexts.FirstOrDefault(c => c.Id == wanContextId);
        if (context == null)
            return (null, "The WAN this test was set up for no longer exists. Pick a WAN and save it again.");

        if (context.AgentId == null)
            return (null, $"'{context.Name}' is measured by this server over a bound source address, not by an agent, so it has no agent to run a speed test on.");

        if (!connectedAgentIds.Contains(context.AgentId.Value))
            return (null, $"The agent for '{context.Name}' is not connected. WAN speed tests on that WAN resume when it reconnects.");

        if (!capableAgentIds.Contains(context.AgentId.Value))
            return (null, $"The agent for '{context.Name}' runs on the gateway, which carries no speed test binary. Use the Gateway test for that WAN.");

        return (new AgentWanTestVantage(context.AgentId.Value, context), null);
    }

    /// <summary>Lowest-id connected agent that can run the test, so the choice does not move between runs.</summary>
    private async Task<int?> FirstCapableAgentAsync(string siteSlug, CancellationToken ct)
    {
        foreach (var agentId in _tunnels.GetForSite(siteSlug).Select(c => c.AgentId).OrderBy(id => id))
            if (await CanRunAsync(siteSlug, agentId, ct))
                return agentId;
        return null;
    }

    /// <summary>
    /// Resolves one agent against its site's gateway addresses. The live tunnel reports every
    /// address its host holds; an agent with no open tunnel falls back to its last known LAN IP.
    /// <para>
    /// A site with one connected agent asks the SITE-level verdict instead, which is the question
    /// every WAN test surface asked before there was a per-agent one, and is unambiguous when there
    /// is only one agent to be about. That is not a preference: the two verdicts persist under
    /// different keys, so an agent enrolled before the per-agent key existed has nothing stored to
    /// fall back on, and a cold read with the console still down answers "not on the gateway"
    /// (TODO(#1106) in AgentOnGatewayDetector). Routing single-agent sites through the site-level
    /// answer means the only installs that exist today cannot behave differently than they did.
    /// </para>
    /// </summary>
    private async Task<bool> IsOnGatewayAsync(string siteSlug, int agentId, CancellationToken ct)
    {
        if (_tunnels.GetForSite(siteSlug).Count <= 1)
            return await _onGateway.IsAgentOnGatewayAsync(siteSlug, ct);

        var candidates = _tunnels.GetForSite(siteSlug).FirstOrDefault(c => c.AgentId == agentId)?.HostAddresses;
        if (candidates is not { Count: > 0 })
        {
            var lanIp = await LookupLanIpAsync(agentId, ct);
            candidates = string.IsNullOrEmpty(lanIp) ? Array.Empty<string>() : new[] { lanIp };
        }
        return await _onGateway.IsAgentOnGatewayAsync(siteSlug, agentId, candidates, ct);
    }

    private async Task<string?> LookupLanIpAsync(int agentId, CancellationToken ct)
    {
        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(ct);
            return await db.SiteAgents.AsNoTracking()
                .Where(a => a.Id == agentId).Select(a => a.LanIp).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the LAN IP for agent {AgentId}", agentId);
            return null;
        }
    }

    private async Task<List<WanContext>> LoadContextsAsync(string siteSlug, CancellationToken ct)
    {
        try
        {
            await using var db = _siteDbFactory.CreateForSite(
                siteSlug, siteSlug == SiteManagementService.DefaultSiteSlug);
            return await db.WanContexts.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // A site mid-creation or mid-removal reads as "no contexts", which is the single-WAN
            // path every install takes today.
            _logger.LogDebug(ex, "Could not read WAN contexts for site {Slug}", siteSlug);
            return new List<WanContext>();
        }
    }
}
